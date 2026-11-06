using System.Net.Http.Json;

namespace Starlight.Infrastructure;

public sealed record SocialEndpoint(int Schema, string? Url);

public static class SocialDiscovery
{
    public const string ConfigurationUrl = "https://rostfrolov2021-jpg.github.io/Starlight-Launcher/social.json";
    public static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
    public static Uri? Validate(SocialEndpoint? endpoint)
    {
        if (endpoint?.Schema != 1 || string.IsNullOrWhiteSpace(endpoint.Url)) return null;
        var uri = ApiClient.ServiceUri(endpoint.Url);
        if (uri.Scheme != "https" || uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Общий сервис друзей должен иметь публичный HTTPS-адрес.");
        return uri;
    }
    public static async Task<Uri?> ResolveAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(ConfigurationUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(16 * 1024).WaitAsync(ct);
        return Validate(await response.Content.ReadFromJsonAsync<SocialEndpoint>(ct));
    }
}
