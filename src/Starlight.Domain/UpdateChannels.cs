namespace Starlight.Domain;

public static class UpdateChannels
{
    public const string GitHubRepository = "rostfrolov2021-jpg/Starlight-Launcher";
    public const string OfficialFeed = "https://github.com/" + GitHubRepository + "/releases/latest/download/latest.json";

    public static string Migrate(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return OfficialFeed;
        if (Uri.TryCreate(saved.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback &&
            uri.AbsolutePath.TrimEnd('/').Equals("/api/updates/latest", StringComparison.OrdinalIgnoreCase)) return OfficialFeed;
        return saved.Trim();
    }
}
