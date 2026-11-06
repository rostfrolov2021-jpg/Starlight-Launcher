using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Starlight.Domain;
using Starlight.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.AddSingleton<SocialStore>();
builder.Services.AddSingleton<AccountStorage>();
builder.Services.AddSingleton<RelayRooms>();
builder.Services.AddSingleton<NatMediator>();
builder.Services.AddHostedService(s => s.GetRequiredService<NatMediator>());
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("catalog", client => { client.Timeout = TimeSpan.FromSeconds(30); client.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightService/1.1"); });
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 24 * 1024; o.MaximumParallelInvocationsPerClient = 1; });
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("catalog", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseRateLimiter();
app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/hub") || context.Request.Path.StartsWithSegments("/relay"))
    {
        var token = context.Request.Headers.Authorization.ToString();
        if (token.StartsWith("Bearer ", StringComparison.Ordinal)) token = token[7..];
        var user = app.Services.GetRequiredService<SocialStore>().Authenticate(token);
        if (user == null) { context.Response.StatusCode = 401; return; }
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Name)], "Starlight"));
    }
    await next();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Starlight" }));
app.MapClientApi();
app.MapPost("/auth/register", async (SocialLogin login, SocialStore store) => {
    try { return Results.Ok(await store.RegisterAsync(login)); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
}).RequireRateLimiting("auth");
app.MapPost("/auth/login", async (SocialLogin login, SocialStore store) => {
    var session = await store.LoginAsync(login); return session == null ? Results.Unauthorized() : Results.Ok(session);
}).RequireRateLimiting("auth");
app.MapHub<SocialHub>("/hub");
app.Map("/relay/{room}/{stream}/{role}", RelayRooms.HandleAsync);
await app.Services.GetRequiredService<SocialStore>().InitializeAsync();
app.Run();

namespace Starlight.Server
{
    public sealed record User(string Id, string Name, string Salt, string Hash, HashSet<string> Friends, HashSet<string> Requests);
    public sealed class SocialStore
    {
        private readonly AccountStorage storage;
        private readonly SemaphoreSlim gate = new(1);
        private Dictionary<string, User> users = [];
        private readonly ConcurrentDictionary<string, (string Id, DateTimeOffset Expires)> tokens = new();
        public ConcurrentDictionary<string, Presence> Online { get; } = new();
        public SocialStore(AccountStorage storage) { this.storage = storage; }
        public async Task InitializeAsync() => users = await storage.LoadAsync();
        public User? Authenticate(string token)
        {
            if (!tokens.TryGetValue(token, out var value)) return null;
            if (value.Expires < DateTimeOffset.UtcNow) { tokens.TryRemove(token, out _); return null; }
            return Volatile.Read(ref users).GetValueOrDefault(value.Id);
        }
        public User Get(string id) => Volatile.Read(ref users).GetValueOrDefault(id) ?? throw new HubException("Пользователь не найден.");
        private SocialSession Session(User user)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); tokens[token] = (user.Id, DateTimeOffset.UtcNow.AddHours(12));
            return new(user.Id, user.Name, token);
        }
        public async Task<SocialSession> RegisterAsync(SocialLogin login)
        {
            if (!Regex.IsMatch(login.Name ?? "", "^[A-Za-z0-9_]{3,24}$") || login.Password == null || login.Password.Length is < 12 or > 256) throw new ArgumentException("Имя: 3–24 латинских символа; пароль: 12–256 символов.");
            await gate.WaitAsync();
            try
            {
                if (users.Values.Any(u => u.Name.Equals(login.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Это имя уже занято.");
                var salt = RandomNumberGenerator.GetBytes(32);
                var hash = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(login.Password, salt, 600_000, HashAlgorithmName.SHA256, 32));
                var user = new User(Guid.NewGuid().ToString("N"), login.Name!, Convert.ToHexString(salt), Convert.ToHexString(hash), [], []);
                var updated = new Dictionary<string, User>(users) { [user.Id] = user }; await SaveAsync(updated); return Session(user);
            }
            finally { gate.Release(); }
        }
        public async Task<SocialSession?> LoginAsync(SocialLogin login)
        {
            if (login.Password == null || login.Password.Length > 256) return null;
            var user = Volatile.Read(ref users).Values.FirstOrDefault(u => u.Name.Equals(login.Name, StringComparison.OrdinalIgnoreCase));
            var salt = user == null ? new byte[32] : Convert.FromHexString(user.Salt);
            var hash = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(login.Password, salt, 600_000, HashAlgorithmName.SHA256, 32));
            return user != null && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(user.Hash)) ? Session(user) : null;
        }
        public User[] Outgoing(string id) => Volatile.Read(ref users).Values.Where(u => u.Requests.Contains(id) && !u.Friends.Contains(id)).ToArray();
        public SocialUser[] Search(string from, string prefix)
        {
            prefix = (prefix ?? "").Trim();
            if (!Regex.IsMatch(prefix, "^[A-Za-z0-9_]{3,24}$")) return [];
            return Volatile.Read(ref users).Values.Where(u => u.Id != from && u.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Take(20).Select(u => new SocialUser(u.Id, u.Name)).ToArray();
        }
        public async Task<string> RequestAsync(string from, string name)
        {
            try { name = FriendLookup.Normalize(name); } catch (ArgumentException e) { throw new HubException(e.Message); }
            await gate.WaitAsync();
            try
            {
                var target = users.Values.FirstOrDefault(u => u.Id == name || u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new HubException("Аккаунт Starlight не найден на этом сервисе. Проверьте имя или ID и убедитесь, что вы с другом подключены к одному сервису друзей.");
                if (target.Id == from) throw new HubException("Нельзя добавить самого себя.");
                if (target.Friends.Contains(from)) throw new HubException("Этот пользователь уже в друзьях.");
                if (target.Requests.Contains(from)) return target.Id;
                var updated = new Dictionary<string, User>(users) { [target.Id] = target with { Requests = new(target.Requests) { from } } }; await SaveAsync(updated);
                return target.Id;
            }
            finally { gate.Release(); }
        }
        public async Task AcceptAsync(string id, string other)
        {
            await gate.WaitAsync();
            try
            {
                var user = Get(id); var friend = Get(other);
                if (!user.Requests.Contains(other)) throw new HubException("Нет входящей заявки.");
                var requests = new HashSet<string>(user.Requests); requests.Remove(other);
                var reverseRequests = new HashSet<string>(friend.Requests); reverseRequests.Remove(id);
                var updated = new Dictionary<string, User>(users) { [id] = user with { Friends = new(user.Friends) { other }, Requests = requests }, [other] = friend with { Friends = new(friend.Friends) { id }, Requests = reverseRequests } };
                await SaveAsync(updated);
            }
            finally { gate.Release(); }
        }
        public async Task RemoveAsync(string id, string other)
        {
            await gate.WaitAsync();
            try
            {
                var a = Get(id); var b = Get(other); var af = new HashSet<string>(a.Friends); var bf = new HashSet<string>(b.Friends); var ar = new HashSet<string>(a.Requests);
                var br = new HashSet<string>(b.Requests);
                af.Remove(other); bf.Remove(id); ar.Remove(other); br.Remove(id);
                await SaveAsync(new(users) { [id] = a with { Friends = af, Requests = ar }, [other] = b with { Friends = bf, Requests = br } });
            }
            finally { gate.Release(); }
        }
        private async Task SaveAsync(Dictionary<string, User> updated)
        {
            try { await storage.SaveAsync(updated); }
            catch (AccountStorageConflictException) { Volatile.Write(ref users, await storage.LoadAsync()); throw new HubException("Сервис обновляется. Повторите действие."); }
            Volatile.Write(ref users, updated);
        }
        public bool AreFriends(string a, string b) => Get(a).Friends.Contains(b);
    }
}
