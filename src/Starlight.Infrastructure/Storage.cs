using System.Security.Cryptography;
using System.Text.Json;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public sealed class PathService
{
    public string Root { get; }
    public string Config => Path.Combine(Root, "config");
    public string Runtimes => Path.Combine(Root, "runtimes");
    public string Instances => Path.Combine(Root, "instances");
    public string Servers => Path.Combine(Root, "local_servers");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public PathService(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.UserProfile), ".starlight"));
        foreach (var dir in new[] { Config, Runtimes, Instances, Servers, Cache, Logs }) Directory.CreateDirectory(dir);
    }
    public string InstanceRoot(Guid id) => Path.Combine(Instances, id.ToString("N"));
    public string Game(Guid id) => Path.Combine(InstanceRoot(id), ".minecraft");
    public string Mods(Guid id) => Path.Combine(Game(id), "mods");
    public string Server(Guid id) => Path.Combine(Servers, id.ToString("N"));
    public static string SafeChild(string root, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(['/', '\\', ':']) >= 0 || file is "." or ".." || Path.GetFileName(file) != file)
            throw new InvalidDataException("Небезопасное имя файла.");
        return Path.Combine(root, file);
    }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct);
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                await JsonSerializer.SerializeAsync(stream, value, Options, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class InstanceService(PathService paths)
{
    public async Task<List<Instance>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<Instance>();
        foreach (var dir in Directory.EnumerateDirectories(paths.Instances))
        {
            var instance = await JsonStore.ReadAsync<Instance>(Path.Combine(dir, "instance.json"), ct);
            if (instance != null) result.Add(instance);
        }
        return result;
    }
    public async Task<Instance> CreateAsync(string name, string version, ModLoader loader, int memory, string icon, CancellationToken ct, string? loaderVersion = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Имя сборки: от 1 до 80 символов.");
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Выберите версию Minecraft.");
        if (memory is < 512 or > 65536) throw new ArgumentException("Память: 512–65536 МБ.");
        var instance = new Instance(Guid.NewGuid(), name.Trim(), version, loader, LoaderVersion: string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim(), MemoryMb: memory, Icon: icon);
        foreach (var child in new[] { "mods", "config", "saves", "resourcepacks" }) Directory.CreateDirectory(Path.Combine(paths.Game(instance.Id), child));
        await SaveAsync(instance, ct);
        return instance;
    }
    public Task SaveAsync(Instance instance, CancellationToken ct = default) => JsonStore.WriteAsync(Path.Combine(paths.InstanceRoot(instance.Id), "instance.json"), instance, ct);
}

// A user-supplied passphrase is never persisted. Tokens are encrypted on every platform.
public sealed class AccountVault(PathService paths)
{
    private string FilePath => Path.Combine(paths.Config, "accounts.enc");
    public async Task<List<Account>> ReadAsync(string password, CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return [];
        var data = await File.ReadAllBytesAsync(FilePath, ct);
        if (data.Length < 60) throw new InvalidDataException("Повреждено хранилище аккаунтов.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(0, 32), 600_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[data.Length - 60];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(32, 12), data.AsSpan(60), data.AsSpan(44, 16), plain);
            return JsonSerializer.Deserialize<List<Account>>(plain, JsonStore.Options) ?? [];
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task SaveAsync(IReadOnlyList<Account> accounts, string password, CancellationToken ct = default)
    {
        if (password.Length < 10) throw new ArgumentException("Пароль хранилища должен содержать минимум 10 символов.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts, JsonStore.Options);
        var data = new byte[60 + plain.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, 44));
        var key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(0, 32), 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(data.AsSpan(32, 12), plain, data.AsSpan(60), data.AsSpan(44, 16));
            var temp = FilePath + ".tmp";
            await File.WriteAllBytesAsync(temp, data, ct);
            File.Move(temp, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
}
