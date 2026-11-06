using System.Text.RegularExpressions;
using Starlight.Domain;

namespace Starlight.Infrastructure;

public static partial class ServerProperties
{
    public static ServerOptions Read(string path, bool bedrock = false)
    {
        var values = new Dictionary<string,string>();
        foreach (var line in File.Exists(path) ? File.ReadAllLines(path) : [])
        {
            var text = line.Trim(); if (text.StartsWith('#') || text.StartsWith('!')) continue;
            var at = text.IndexOf('='); if (at > 0) values[text[..at].Trim()] = text[(at + 1)..].Trim();
        }
        return new(values.GetValueOrDefault(bedrock ? "xbox-auth" : "online-mode", "true") is "true" or "on", values.GetValueOrDefault("server-ip", "127.0.0.1") != "127.0.0.1",
            values.GetValueOrDefault("white-list", "false") == "true", int.TryParse(values.GetValueOrDefault("max-players"), out var max) ? max : 20, values.GetValueOrDefault("motd", "Starlight server"));
    }
    public static async Task WriteAsync(string path, ServerOptions options, int port, CancellationToken ct, bool bedrock = false)
    {
        if (port is < 1024 or > 65535 || options.MaxPlayers is < 1 or > 1000 || options.Motd.Length > 160 || options.Motd.Contains('\n') || options.Motd.Contains('\r')) throw new ArgumentException("Проверьте порт, число игроков и описание сервера.");
        var changes = new Dictionary<string,string> { [bedrock ? "xbox-auth" : "online-mode"] = options.OnlineMode ? "true" : "false", ["server-ip"] = options.NetworkAccess ? "" : "127.0.0.1", ["server-port"] = port.ToString(), ["white-list"] = options.WhiteList ? "true" : "false", ["max-players"] = options.MaxPlayers.ToString(), ["motd"] = options.Motd };
        var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path, ct)).ToList() : [];
        var output = new List<string>(); var written = new HashSet<string>();
        foreach (var line in lines)
        {
            var at = line.IndexOf('='); var key = at > 0 ? line[..at].Trim() : "";
            if (!changes.TryGetValue(key, out var value)) output.Add(line);
            else if (written.Add(key)) output.Add(key + "=" + value);
        }
        foreach (var item in changes) if (written.Add(item.Key)) output.Add(item.Key + "=" + item.Value);
        var temp = path + ".tmp";
        try { await File.WriteAllLinesAsync(temp, output, ct); if (File.Exists(path)) File.Copy(path, path + ".starlight.bak", true); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Command(string text)
    {
        var value = text.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Введите одну команду без переноса строки.");
        return value;
    }
    public static string Player(string name)
    {
        if (!PlayerName().IsMatch(name)) throw new ArgumentException("Ник игрока: 3–16 латинских букв, цифр или _. Для других имён используйте консоль.");
        return name;
    }
    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")] private static partial Regex PlayerName();
}
