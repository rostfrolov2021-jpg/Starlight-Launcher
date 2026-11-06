using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Starlight.Server;

public sealed class RelayRooms
{
    private sealed record Room(string Owner, DateTimeOffset Created);
    private sealed class Pair(string guest)
    {
        public string Guest { get; } = guest;
        public TaskCompletionSource<WebSocket> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Joined;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }
    private readonly ConcurrentDictionary<string, Room> rooms = new();
    private readonly ConcurrentDictionary<string, Pair> pairs = new();
    public string Create(string owner) { RemoveOwner(owner); var id = Guid.NewGuid().ToString("N"); rooms[id] = new(owner, DateTimeOffset.UtcNow); return id; }
    public bool IsOwner(string room, string owner) => rooms.TryGetValue(room, out var r) && r.Owner == owner;
    public void RemoveOwner(string owner)
    {
        foreach (var room in rooms.Where(x => x.Value.Owner == owner))
        {
            rooms.TryRemove(room.Key, out _);
            foreach (var pair in pairs.Where(x => x.Key.StartsWith(room.Key + ":", StringComparison.Ordinal)))
                if (pairs.TryRemove(pair.Key, out var p)) { if (p.Host.Task.IsCompletedSuccessfully) p.Host.Task.Result.Abort(); p.Done.TrySetResult(); }
        }
    }
    public string Authorize(string room, string guest, SocialStore store)
    {
        if (!rooms.TryGetValue(room, out var r) || !store.AreFriends(r.Owner, guest)) throw new HubException("Комната недоступна."); return r.Owner;
    }
    public void Reserve(string room, string stream, string guest)
    {
        foreach (var stale in pairs.Where(x => x.Value.Created < DateTimeOffset.UtcNow.AddSeconds(-45) && !x.Value.Host.Task.IsCompleted)) pairs.TryRemove(stale.Key, out _);
        if (!Guid.TryParseExact(stream, "N", out _) || pairs.Count(x => x.Value.Guest == guest) >= 16 || !pairs.TryAdd(room + ":" + stream, new Pair(guest))) throw new HubException("Лимит соединений.");
    }
    public static async Task HandleAsync(HttpContext context)
    {
        var self = context.RequestServices.GetRequiredService<RelayRooms>();
        var store = context.RequestServices.GetRequiredService<SocialStore>();
        var room = context.Request.RouteValues["room"]?.ToString() ?? ""; var stream = context.Request.RouteValues["stream"]?.ToString() ?? "";
        var role = context.Request.RouteValues["role"]?.ToString(); var user = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var key = room + ":" + stream;
        if (!context.WebSockets.IsWebSocketRequest || !self.pairs.TryGetValue(key, out var pair)) { context.Response.StatusCode = 404; return; }
        if (role == "host" ? !self.IsOwner(room, user) : role != "guest" || pair.Guest != user) { context.Response.StatusCode = 403; return; }
        if (role == "guest") { try { self.Authorize(room, user, store); } catch (HubException) { context.Response.StatusCode = 403; return; } }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromHours(12));
        try
        {
            if (role == "host")
            {
                if (!pair.Host.TrySetResult(socket)) return;
                await pair.Done.Task.WaitAsync(timeout.Token).WaitAsync(TimeSpan.FromHours(12));
            }
            else
            {
                if (Interlocked.Exchange(ref pair.Joined, 1) != 0) return;
                var host = await pair.Host.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
                var first = PumpAsync(socket, host, timeout.Token); var second = PumpAsync(host, socket, timeout.Token);
                await Task.WhenAny(first, second); await timeout.CancelAsync();
                try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or TimeoutException) { }
        finally { if (role == "guest") { pair.Done.TrySetResult(); self.pairs.TryRemove(key, out _); } socket.Abort(); }
    }
    private static async Task PumpAsync(WebSocket input, WebSocket output, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = await input.ReceiveAsync(buffer.AsMemory(), ct); if (read.MessageType == WebSocketMessageType.Close) break;
            if (read.MessageType != WebSocketMessageType.Binary) throw new WebSocketException("Binary only");
            await output.SendAsync(buffer.AsMemory(0, read.Count), WebSocketMessageType.Binary, read.EndOfMessage, ct);
        }
    }
}
