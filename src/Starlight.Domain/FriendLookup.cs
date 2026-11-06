using System.Text.RegularExpressions;

namespace Starlight.Domain;

public static class FriendLookup
{
    public static string Normalize(string? value)
    {
        var text = (value ?? "").Trim();
        if (Guid.TryParse(text, out var id)) return id.ToString("N");
        if (Regex.IsMatch(text, "^[A-Za-z0-9_]{3,24}$")) return text;
        throw new ArgumentException("Введите имя аккаунта Starlight или его ID. Ник Minecraft и Client ID Microsoft не подходят.");
    }
}
