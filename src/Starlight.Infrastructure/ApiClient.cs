using System.Net;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class ApiClient(HttpClient http)
{
    public async Task<JsonElement> GetAsync(string url, CancellationToken ct, string? curseForgeKey = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (curseForgeKey != null) request.Headers.Add("x-api-key", curseForgeKey);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 15)), ct); continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("Каталог отклонил запрос. Проверьте ключ API или подключение сервиса.");
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"API {new Uri(url).Host}: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(input, cancellationToken: timeout.Token);
                return document.RootElement.Clone();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 2) { }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode == null || (int)ex.StatusCode >= 500)) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
    public static Uri ServiceUri(string text)
    {
        if (!Uri.TryCreate(text.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Укажите HTTPS-адрес сервиса. Для локального сервера допустим http://localhost:5088.");
        return uri.Scheme == "http" && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri : uri;
    }
    public async Task<ServiceInfo> ConnectAsync(string address, CancellationToken ct)
    {
        var json = await GetAsync(new Uri(ServiceUri(address), "api/client-config").AbsoluteUri, ct);
        var info = json.Deserialize<ServiceInfo>(JsonStore.Options);
        if (info?.Service != "Starlight" || info.NatPort is < 1024 or > 65535) throw new InvalidDataException("По этой ссылке не найден совместимый сервис Starlight.");
        return info;
    }
}
