using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Starlight.Domain;

public sealed record ReleaseArtifact(string Runtime, string Url, string Sha256, long Size);
public sealed record ReleaseManifest(string Version, string Notes, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseArtifact> Artifacts);
public sealed record SignedRelease(string Payload, string Signature);
public sealed record UpdateRequest(string Package, string Envelope, string Target, int ParentPid, string Runtime, bool Restart = true);
public static class ReleaseTrust
{
    public static string PublicKey
    {
        get { using var reader = new StreamReader(typeof(ReleaseTrust).Assembly.GetManifestResourceStream("Starlight.ReleasePublicKey") ?? throw new InvalidOperationException("Не задан ключ обновлений.")); return reader.ReadToEnd(); }
    }
    public static ReleaseManifest Verify(SignedRelease envelope, string? publicKey = null)
    {
        var payload = Convert.FromBase64String(envelope.Payload);
        if (payload.Length > 128 * 1024) throw new InvalidDataException("Слишком большой манифест обновления.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey ?? PublicKey);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new CryptographicException("Подпись обновления не принадлежит Starlight.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Некорректное обновление.");
        if (!Version.TryParse(manifest.Version, out _) || manifest.Artifacts.Count is < 1 or > 12) throw new InvalidDataException("Некорректная версия обновления.");
        foreach (var artifact in manifest.Artifacts)
            if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit) || artifact.Size is < 1 or > 2_000_000_000) throw new InvalidDataException("Некорректный пакет обновления.");
        return manifest;
    }
}
