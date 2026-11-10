using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class SocialClient(HttpClient http) : IAsyncDisposable
{
    public HubConnection? Hub { get; private set; }
    public SocialSession? Session { get; private set; }
    public Uri? BaseUri { get; private set; }
    public event Action<ChatMessage>? Message;
    public event Action? FriendsChanged;
    public event Action<string>? GroupCreated;
    public event Action<string, string, string>? VoiceOffer;
    public event Action<string>? Notice;
    private CancellationTokenSource tunnels = new();
    private int hostPort;
    private int hostPackPort;
    private string? hostRoom;
    private TcpListener? listener;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> connections = new();
    public int NatPort { get; set; } = 5089;
    public bool EnablePeerDiscovery { get; set; } = true;
    private Func<int, IPEndPoint?, CancellationToken, Task<IPEndPoint[]>>? CandidateExchange(string room, string stream)
        => !EnablePeerDiscovery ? null : async (port, mapped, ct) => {
            var candidates = new List<string>(); if (mapped != null) candidates.Add(mapped.ToString());
            if (BaseUri!.IsLoopback) candidates.Add(new IPEndPoint(IPAddress.Loopback, port).ToString());
            else foreach (var address in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Take(3)) candidates.Add(new IPEndPoint(address, port).ToString());
            var remote = await RequireHub().InvokeAsync<string[]>("ExchangeCandidates", room, stream, candidates.Distinct().Take(4).ToArray(), ct);
            return remote.Take(4).Select(c => IPEndPoint.TryParse(c, out var endpoint) ? endpoint : null).Where(e => e is { Port: >= 1024 } && e.Address.AddressFamily == AddressFamily.InterNetwork && e.Address.GetAddressBytes()[0] is > 0 and < 224).Cast<IPEndPoint>().ToArray();
        };
    public bool Connected => Hub?.State == HubConnectionState.Connected;
    public Task SetGameActivityAsync(bool playing, CancellationToken ct) => RequireHub().InvokeAsync("SetPresence", new Presence(hostRoom != null ? "Игра открыта друзьям" : playing ? "В игре" : "В сети", hostRoom), ct);
    public Task CloseGuestConnectionAsync() => hostRoom == null ? StopTunnelsAsync() : Task.CompletedTask;
    public async Task ConnectAsync(string url, string name, string password, bool register, CancellationToken ct)
    {
        name = name.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,24}$")) throw new ArgumentException("Имя Starlight: 3–24 латинских буквы, цифры или _. Это не email и не Microsoft Client ID.");
        if (string.IsNullOrEmpty(password) || password.Length > 256 || (register && password.Length < 12)) throw new ArgumentException(register ? "Для регистрации нужен пароль от 12 до 256 символов." : "Введите пароль аккаунта Starlight.");
        var uri = ApiClient.ServiceUri(url);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Внешний сервер связи должен использовать HTTPS.");
        await DisconnectAsync(); BaseUri = uri;
        if (!uri.IsLoopback) await WaitForServiceAsync(uri, ct);
        using var response = await http.PostAsJsonAsync(new Uri(uri, register ? "auth/register" : "auth/login"), new SocialLogin(name, password), ct);
        if (!response.IsSuccessStatusCode)
        {
            string? reason = null;
            try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct); if (error.TryGetProperty("error", out var message)) reason = message.GetString(); } catch (JsonException) { }
            throw new InvalidOperationException(reason ?? response.StatusCode switch {
                HttpStatusCode.Unauthorized => "Неверное имя или пароль Starlight. Если аккаунта ещё нет, нажмите «Регистрация».",
                HttpStatusCode.TooManyRequests => "Слишком много попыток входа. Подождите минуту и повторите.",
                HttpStatusCode.NotFound => "По этому адресу нет сервиса аккаунтов Starlight. Привяжите API в настройках.",
                _ => $"Сервис аккаунтов вернул HTTP {(int)response.StatusCode}. Проверьте адрес и состояние сервиса." });
        }
        Session = await response.Content.ReadFromJsonAsync<SocialSession>(ct) ?? throw new InvalidDataException();
        Hub = new HubConnectionBuilder().WithUrl(new Uri(uri, "hub"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(Session.Token)).WithAutomaticReconnect().Build();
        Hub.On<ChatMessage>("Message", m => Message?.Invoke(m)); Hub.On("FriendsChanged", () => FriendsChanged?.Invoke());
        Hub.On<string>("GroupCreated", id => GroupCreated?.Invoke(id));
        Hub.On<string, string, string>("VoiceOffer", (id, endpoint, secret) => VoiceOffer?.Invoke(id, endpoint, secret));
        Hub.On<string, string, string>("OpenTunnel", (room, stream, secret) => {
            if (room == hostRoom) Track(HandleHostAsync(room, stream, secret, hostPort, tunnels.Token));
        });
        Hub.On<string, string, string>("OpenPackTunnel", (room, stream, secret) => {
            if (room == hostRoom && hostPackPort > 0) Track(HandleHostAsync(room, stream, secret, hostPackPort, tunnels.Token));
        });
        Hub.Reconnecting += _ => { Notice?.Invoke("Связь прервана. Переподключение…"); return Task.CompletedTask; };
        Hub.Closed += _ => { Notice?.Invoke("Связь с друзьями потеряна. Нажмите «Войти», чтобы подключиться снова."); FriendsChanged?.Invoke(); return Task.CompletedTask; };
        Hub.Reconnected += async _ => { hostRoom = null; await StopTunnelsAsync(); Notice?.Invoke("Связь восстановлена. Опубликуйте сервер заново."); FriendsChanged?.Invoke(); };
        try { await Hub.StartAsync(ct); }
        catch { await DisconnectAsync(); throw new InvalidOperationException(register ? "Аккаунт создан, но чат не подключился. Попробуйте «Войти»; повторная регистрация не нужна." : "Вход принят, но чат не подключился. Проверьте доступность WebSocket на сервисе."); }
    }
    public Task<Friend[]> FriendsAsync(CancellationToken ct) => RequireHub().InvokeAsync<Friend[]>("Friends", ct);
    private async Task WaitForServiceAsync(Uri uri, CancellationToken ct)
    {
        Notice?.Invoke("Подключение к сервису друзей… После простоя бесплатный хостинг может запускаться около минуты.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(100));
        try
        {
            while (true)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await http.GetAsync(new Uri(uri, "health"), attempt.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var health = await response.Content.ReadFromJsonAsync<JsonElement>(attempt.Token);
                        if (health.TryGetProperty("service", out var service) && service.GetString() == "Starlight" && health.TryGetProperty("status", out var status) && status.GetString() == "ok") return;
                    }
                }
                catch (Exception e) when (!deadline.IsCancellationRequested && e is HttpRequestException or JsonException or OperationCanceledException) { }
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Сервис друзей пока недоступен. Повторите вход через минуту. Локальный аккаунт вместо общего не создавался."); }
    }
    public Task<SocialUser[]> SearchUsersAsync(string prefix, CancellationToken ct) => RequireHub().InvokeAsync<SocialUser[]>("SearchUsers", prefix.Trim(), ct);
    public Task RequestFriendAsync(string value, CancellationToken ct) => RequireHub().InvokeAsync("RequestFriend", FriendLookup.Normalize(value), ct);
    public static string FriendlyError(Exception error)
    {
        var message = error.Message;
        const string marker = "HubException: ";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? message[(index + marker.Length)..].Trim() : error is Microsoft.AspNetCore.SignalR.HubException ? "Не удалось выполнить действие. Проверьте подключение к сервису друзей." : message;
    }
    private void Track(Task task)
    {
        var id = Guid.NewGuid(); connections[id] = task;
        _ = task.ContinueWith(_ => connections.TryRemove(id, out var ignored), TaskScheduler.Default);
    }
    private async Task HandleHostAsync(string room, string stream, string secret, int targetPort, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, targetPort, ct);
            if (await PeerTunnel.TryBridgeAsync(tcp.GetStream(), BaseUri!.Host, NatPort, secret, false, ct, () => Notice?.Invoke("Маршрут: P2P"), CandidateExchange(room, stream))) return;
            Notice?.Invoke("Маршрут: relay");
            using var socket = await SocketAsync(room, stream, "host", ct); await BridgeAsync(tcp.GetStream(), socket, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice?.Invoke("Туннель закрыт: " + e.GetType().Name); }
    }
    public HubConnection RequireHub() => Connected ? Hub! : throw new InvalidOperationException("Подключитесь к серверу Starlight.");
    public async Task<string> HostAsync(int port, string serverName, CancellationToken ct, PublishedFriendPack? pack = null)
    {
        await StopHostingAsync(ct);
        hostPort = port; hostPackPort = pack?.Port ?? 0; hostRoom = await RequireHub().InvokeAsync<string>("HostRoom", ct);
        if (pack != null) await RequireHub().InvokeAsync("SetRoomPack", hostRoom, pack.Description, ct);
        await RequireHub().InvokeAsync("SetPresence", new Presence("Хостит: " + serverName, hostRoom), ct); return hostRoom;
    }
    public async Task StopHostingAsync(CancellationToken ct)
    {
        if (hostRoom != null && Connected) { await Hub!.InvokeAsync("CloseRoom", hostRoom, ct); await Hub!.InvokeAsync("SetPresence", new Presence("В сети"), ct); }
        hostRoom = null; await StopTunnelsAsync();
    }
    public Task<SharedPack?> GetRoomPackAsync(string room, CancellationToken ct) => RequireHub().InvokeAsync<SharedPack?>("GetRoomPack", room, ct);
    public async Task<int> JoinAsync(string room, CancellationToken ct, bool pack = false)
    {
        await StopTunnelsAsync(); listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; _ = AcceptLoopAsync(room, listener, pack, tunnels.Token); ct.ThrowIfCancellationRequested(); return port;
    }
    private async Task AcceptLoopAsync(string room, TcpListener server, bool pack, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await server.AcceptTcpClientAsync(ct); Track(HandleGuestAsync(room, tcp, pack, ct));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
    }
    private async Task HandleGuestAsync(string room, TcpClient tcp, bool pack, CancellationToken ct)
    {
        using (tcp)
        try
        {
            var stream = Guid.NewGuid().ToString("N"); var secret = VoiceService.NewSecret(); await RequireHub().InvokeAsync(pack ? "OpenPackTunnel" : "OpenTunnel", room, stream, secret, ct);
            if (await PeerTunnel.TryBridgeAsync(tcp.GetStream(), BaseUri!.Host, NatPort, secret, true, ct, () => Notice?.Invoke("Маршрут: P2P"), CandidateExchange(room, stream))) return;
            Notice?.Invoke("Маршрут: relay");
            using var socket = await SocketAsync(room, stream, "guest", ct); await BridgeAsync(tcp.GetStream(), socket, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice?.Invoke("Соединение с другом закрыто: " + e.GetType().Name); }
    }
    private async Task<ClientWebSocket> SocketAsync(string room, string stream, string role, CancellationToken ct)
    {
        var uri = new UriBuilder(new Uri(BaseUri!, $"relay/{Uri.EscapeDataString(room)}/{Uri.EscapeDataString(stream)}/{role}")) { Scheme = BaseUri!.Scheme == "https" ? "wss" : "ws" };
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + Session!.Token);
        try { await socket.ConnectAsync(uri.Uri, ct); return socket; } catch { socket.Dispose(); throw; }
    }
    public static async Task BridgeAsync(Stream tcp, WebSocket socket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Upload()
        {
            var buffer = new byte[32 * 1024]; int read;
            while ((read = await tcp.ReadAsync(buffer, stop.Token)) > 0) await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, stop.Token);
        }
        async Task Download()
        {
            var buffer = new byte[32 * 1024];
            while (!stop.IsCancellationRequested)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                if (read.MessageType == WebSocketMessageType.Close) break;
                await tcp.WriteAsync(buffer.AsMemory(0, read.Count), stop.Token);
            }
        }
        var upload = Upload(); var download = Download(); await Task.WhenAny(upload, download); await stop.CancelAsync();
        try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { }
    }
    private async Task StopTunnelsAsync() { await tunnels.CancelAsync(); listener?.Stop(); listener = null; await Task.WhenAll(connections.Values); tunnels.Dispose(); tunnels = new(); }
    public async Task DisconnectAsync()
    {
        await StopTunnelsAsync(); if (Hub != null) { await Hub.DisposeAsync(); Hub = null; } Session = null; hostRoom = null;
    }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); tunnels.Dispose(); }
}
