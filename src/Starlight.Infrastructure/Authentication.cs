using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed partial class Authentication(HttpClient http)
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")] private static partial Regex Nickname();
    public static Account Offline(string name)
    {
        if (!Nickname().IsMatch(name)) throw new ArgumentException("Ник: 3–16 латинских букв, цифр или _.");
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(name, Convert.ToHexString(bytes).ToLowerInvariant(), false);
    }
    public async Task<Account> LoginAsync(string clientId, CancellationToken ct)
    {
        if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("Укажите Microsoft Client ID в настройках.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        ct = timeout.Token;
        var verifier = Base64(RandomNumberGenerator.GetBytes(48));
        var state = Base64(RandomNumberGenerator.GetBytes(32));
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var redirect = $"http://localhost:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
        var query = new Dictionary<string, string> { ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "XboxLive.signin offline_access", ["state"] = state, ["code_challenge"] = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["prompt"] = "select_account" };
        Shell.Open(Authority + "authorize?" + string.Join("&", query.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}")));
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            if (context.Request.QueryString["state"] != state) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
            var code = context.Request.QueryString["code"];
            var message = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Starlight</title><p>You may close this window and return to Starlight.</p>");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(message, ct); context.Response.Close();
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Вход Microsoft отменён или отклонён.");
            var token = await TokenAsync(new() { ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, ct);
            return await MinecraftAsync(token.GetProperty("access_token").GetString()!, token.GetProperty("refresh_token").GetString()!, ct);
        }
    }
    public async Task<Account> LoginWithCodeAsync(string clientId, Action<DeviceLogin> showCode, CancellationToken ct)
    {
        clientId = clientId.Trim();
        if (clientId.Equals("f8cdef31-a31e-4b4a-93e4-5f571e91255a", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Указан Tenant ID Microsoft Services. Нужен Application (client) ID вашего зарегистрированного приложения, а не Directory (tenant) ID.");
        if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("Вход Microsoft ещё не активирован владельцем Starlight. Привяжите сервис с Client ID или укажите свой в настройках.");
        using var response = await http.PostAsync(Authority + "devicecode?mkt=ru-RU", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = clientId, ["scope"] = "XboxLive.signin offline_access" }), ct);
        var device = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!response.IsSuccessStatusCode) throw MicrosoftError(device);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(device.GetProperty("expires_in").GetInt32());
        var verification = device.GetProperty("verification_uri").GetString()!;
        if (!Uri.TryCreate(verification, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host is "microsoft.com" or "login.live.com"))
            throw new InvalidDataException("Microsoft вернул неожиданный адрес подтверждения.");
        showCode(new(device.GetProperty("user_code").GetString()!, verification, expiry));
        var interval = device.TryGetProperty("interval", out var seconds) ? Math.Max(1, seconds.GetInt32()) : 5;
        while (DateTimeOffset.UtcNow < expiry)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var poll = await http.PostAsync(Authority + "token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = clientId, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = device.GetProperty("device_code").GetString()! }), ct);
            var token = await poll.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (poll.IsSuccessStatusCode) return await MinecraftAsync(token.GetProperty("access_token").GetString()!, token.GetProperty("refresh_token").GetString()!, ct);
            var error = token.TryGetProperty("error", out var value) ? value.GetString() : "unknown";
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException(error switch {
                "authorization_declined" or "access_denied" => "Вход Microsoft отменён.",
                "expired_token" => "Код истёк. Нажмите «Войти» ещё раз.",
                "invalid_client" or "unauthorized_client" => "Microsoft не разрешает этому приложению вход. Проверьте Client ID.",
                _ => "Microsoft не завершил вход: " + error });
        }
        throw new InvalidOperationException("Время действия кода истекло. Запросите новый код.");
    }
    public async Task<Account> RefreshAsync(Account account, string clientId, CancellationToken ct)
    {
        if (!account.IsMicrosoft || account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return account;
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new InvalidOperationException("Войдите в Microsoft повторно.");
        var token = await TokenAsync(new() { ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken, ["scope"] = "XboxLive.signin offline_access" }, ct);
        return await MinecraftAsync(token.GetProperty("access_token").GetString()!, token.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : account.RefreshToken, ct);
    }
    private async Task<JsonElement> TokenAsync(Dictionary<string, string> body, CancellationToken ct)
    {
        using var response = await http.PostAsync(Authority + "token", new FormUrlEncodedContent(body), ct);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!response.IsSuccessStatusCode) throw MicrosoftError(result);
        return result;
    }
    public static InvalidOperationException MicrosoftError(JsonElement response)
    {
        var code = response.TryGetProperty("error", out var error) ? error.GetString() : "unknown";
        var description = response.TryGetProperty("error_description", out var detail) ? detail.GetString() ?? "" : "";
        var diagnostic = Regex.Match(description, "AADSTS[0-9]+").Value;
        var explanation = diagnostic == "AADSTS700016" ? "Приложение не найдено. Нужен Application (client) ID, не Tenant ID; приложение должно поддерживать личные Microsoft accounts."
            : code is "unauthorized_client" or "invalid_client" ? "Проверьте Application (client) ID, поддержку личных аккаунтов и Allow public client flows в Microsoft Entra."
            : code == "invalid_grant" ? "Сессия Microsoft истекла или отозвана. Войдите заново по коду."
            : "Microsoft отклонил запрос. Проверьте настройки приложения и повторите вход.";
        return new InvalidOperationException(explanation + (diagnostic.Length > 0 ? " Код: " + diagnostic + "." : " Ошибка: " + code + "."));
    }
    private async Task<JsonElement> PostAsync(string url, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Авторизация Xbox/Minecraft: HTTP {(int)response.StatusCode}. Проверьте наличие Xbox-профиля, лицензии Java и разрешение приложения.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private async Task<Account> MinecraftAsync(string microsoft, string refresh, CancellationToken ct)
    {
        var xbox = await PostAsync("https://user.auth.xboxlive.com/user/authenticate", new { Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoft }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" }, ct);
        var xsts = await PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new { Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xbox.GetProperty("Token").GetString()! } }, RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT" }, ct);
        var claims = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var uhs = claims.GetProperty("uhs").GetString();
        var mc = await PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = $"XBL3.0 x={uhs};{xsts.GetProperty("Token").GetString()}" }, ct);
        var access = mc.GetProperty("access_token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("У аккаунта нет профиля Minecraft Java. Проверьте лицензию и создайте профиль.");
        response.EnsureSuccessStatusCode(); var profile = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var skins = profile.GetProperty("skins");
        return new(profile.GetProperty("name").GetString()!, profile.GetProperty("id").GetString()!, true, access, refresh,
            DateTimeOffset.UtcNow.AddSeconds(mc.GetProperty("expires_in").GetInt32()), skins.GetArrayLength() > 0 ? skins[0].GetProperty("url").GetString() : null,
            claims.TryGetProperty("xid", out var xid) ? xid.GetString() : null);
    }
    private static string Base64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class Shell
{
    public static void Open(string target)
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(target) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(target);
        Process.Start(start)?.Dispose();
    }
}
