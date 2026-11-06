using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Starlight.Domain;

// Runs from the cache, so the installed executable and its runtime can be replaced after exit.
if (args.Length != 1) return 2;
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var requestPath = Path.GetFullPath(args[0]);
var work = Path.GetDirectoryName(requestPath)!;
var log = Path.Combine(work, "updater.log");
try
{
    var request = JsonSerializer.Deserialize<UpdateRequest>(await File.ReadAllTextAsync(requestPath), options) ?? throw new InvalidDataException();
    var envelope = JsonSerializer.Deserialize<SignedRelease>(await File.ReadAllTextAsync(request.Envelope), options) ?? throw new InvalidDataException();
    var release = ReleaseTrust.Verify(envelope);
    var artifact = release.Artifacts.Single(a => a.Runtime == request.Runtime);
    var target = Path.GetFullPath(request.Target);
    var launcher = OperatingSystem.IsWindows() ? "Starlight.exe" : "Starlight";
    if (Path.GetPathRoot(target) == target || !File.Exists(Path.Combine(target, launcher))) throw new InvalidDataException("Каталог лаунчера не найден.");
    var currentAssembly = Path.Combine(target, "Starlight.Infrastructure.dll");
    if (File.Exists(currentAssembly) && System.Reflection.AssemblyName.GetAssemblyName(currentAssembly).Version is { } current && Version.Parse(release.Version) <= current)
        throw new InvalidDataException("Обновление должно быть новее установленного выпуска.");
    await using (var stream = File.OpenRead(request.Package))
        if (stream.Length != artifact.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Повреждён пакет обновления.");
    using var zip = ZipFile.OpenRead(request.Package);
    if (zip.Entries.Count is < 1 or > 20_000 || zip.GetEntry(launcher) == null) throw new InvalidDataException("В пакете нет лаунчера.");
    var unpacked = Path.Combine(work, "unpacked"); var backup = Path.Combine(work, "backup"); Directory.CreateDirectory(unpacked); Directory.CreateDirectory(backup);
    long expanded = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in zip.Entries)
    {
        var relative = entry.FullName.Replace('\\', '/');
        if (relative.EndsWith('/')) continue;
        if (!names.Add(relative) || relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' ')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new InvalidDataException("Недопустимый путь в обновлении.");
        expanded = checked(expanded + entry.Length); if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Пакет слишком большой.");
        var destination = Path.GetFullPath(Path.Combine(unpacked, relative));
        if (!destination.StartsWith(unpacked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination);
        if (!OperatingSystem.IsWindows()) { var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1ff); if (mode != 0) File.SetUnixFileMode(destination, mode); }
    }
    try { using var parent = Process.GetProcessById(request.ParentPid); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
    catch (ArgumentException) { }
    var replaced = new List<string>(); var created = new List<string>();
    try
    {
        foreach (var relative in names)
        {
            var destination = Path.Combine(target, relative);
            for (var ancestor = Path.GetDirectoryName(destination); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в каталоге установки не поддерживаются.");
                if (ancestor.Equals(target, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в каталоге установки не поддерживаются.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                var old = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.Move(destination, old); replaced.Add(relative);
            }
            created.Add(relative); File.Copy(Path.Combine(unpacked, relative), destination);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(Path.Combine(unpacked, relative)));
        }
    }
    catch
    {
        foreach (var relative in created) File.Delete(Path.Combine(target, relative));
        foreach (var relative in replaced) File.Move(Path.Combine(backup, relative), Path.Combine(target, relative), true);
        throw;
    }
    await File.WriteAllTextAsync(log, "Installed " + release.Version + " successfully. Previous files: " + backup);
    if (request.Restart) Process.Start(new ProcessStartInfo(Path.Combine(target, launcher)) { UseShellExecute = true, WorkingDirectory = target })?.Dispose();
    return 0;
}
catch (Exception error) { await File.WriteAllTextAsync(log, "Update failed; original files restored where necessary.\n" + error); return 1; }
