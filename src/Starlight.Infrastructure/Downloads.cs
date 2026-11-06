using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class Downloads(HttpClient http)
{
    public HttpClient Http => http;
    public async Task<JsonElement> JsonAsync(string url, CancellationToken ct)
        => await new ApiClient(http).GetAsync(url, ct);
    public async Task FileAsync(string url, string destination, string? hash, string algorithm,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("Загрузка разрешена только через HTTPS.");
        if (hash != null)
        {
            var expectedLength = algorithm.ToUpperInvariant() switch { "SHA1" => 40, "SHA256" => 64, "SHA512" => 128, _ => throw new ArgumentException("Неизвестный алгоритм хеширования.") };
            if (hash.Length != expectedLength || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректная контрольная сумма в метаданных.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && hash != null && await VerifyAsync(destination, hash, algorithm, ct)) return;
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long received = 0; var total = response.Content.Headers.ContentLength;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); received += read;
                    progress?.Report(new(Path.GetFileName(destination), total > 0 ? received * 100d / total.Value : 0));
                }
            }
            if (hash != null && !await VerifyAsync(temp, hash, algorithm, ct)) throw new InvalidDataException("Контрольная сумма загрузки не совпадает.");
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<bool> VerifyAsync(string file, string expected, string algorithm, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        byte[] actual = algorithm.ToUpperInvariant() switch
        {
            "SHA1" => await SHA1.HashDataAsync(stream, ct),
            "SHA256" => await SHA256.HashDataAsync(stream, ct),
            "SHA512" => await SHA512.HashDataAsync(stream, ct),
            _ => throw new ArgumentException("Неизвестный алгоритм хеширования.")
        };
        return string.Equals(Convert.ToHexString(actual), expected, StringComparison.OrdinalIgnoreCase);
    }
}
