namespace Starlight.Domain;

public enum ModLoader { Vanilla, Fabric, Forge, NeoForge }
public enum ServerKind { Vanilla, Fabric, Paper, Spigot, Forge, Nukkit, Arclight, Youer }
public sealed record Instance(Guid Id, string Name, string MinecraftVersion, ModLoader Loader,
    string? LoaderVersion = null, int MemoryMb = 4096, string Icon = "✦", string? InstalledVersion = null,
    string? JvmArgs = null, int? JavaMajor = null);
/// <summary>Everything a platform runtime needs to start the game: java binary/home, full argument list, working dir.</summary>
public sealed record LaunchPlan(string JavaPath, IReadOnlyList<string> Arguments, string WorkingDirectory);
/// <summary>Parallelism of the game file installer. Phones need far lower values than desktops to avoid OOM.</summary>
public sealed record InstallTuning(int Checkers, int Downloaders, int Capacity);
public sealed record Account(string Name, string Uuid, bool IsMicrosoft, string? AccessToken = null,
    string? RefreshToken = null, DateTimeOffset? ExpiresAt = null, string? SkinUrl = null, string? Xuid = null)
{
    public override string ToString() => $"{Name} · {(IsMicrosoft ? "Microsoft" : "Offline")}";
}
public sealed record LauncherSettings(string MicrosoftClientId = "", string CurseForgeApiKey = "",
    string SocialUrl = "http://localhost:5088", int MemoryMb = 4096, string Contact = "",
    Guid? SelectedInstance = null, string? SelectedAccount = null, int NatPort = 5089,
    string UpdateFeedUrl = UpdateChannels.OfficialFeed, bool CheckUpdatesOnStartup = true, bool ServiceLinked = false);
public sealed record GameVersion(string Id, string Type, string Url);
public sealed record LocalMod(string FileName, string FullPath, bool Enabled, long Size)
{
    public string Label => $"{(Enabled ? "●" : "○")} {FileName}  ·  {Size / 1024:N0} KB";
}
public sealed record ModProject(string Source, string Id, string Name, string Description, string? IconUrl)
{
    public override string ToString() => $"{Name} · {Source}";
}
public sealed record ModDependency(string ProjectId, string? VersionId = null);
public sealed record ModFile(string ProjectId, string VersionId, string FileName, string Url,
    string Hash, string HashAlgorithm, IReadOnlyList<ModDependency> Dependencies);
public sealed record TransferProgress(string Message, double Percent);
public sealed record LocalServer(Guid Id, string Name, string Version, ServerKind Kind, int Port = 25565, int MemoryMb = 2048);
public sealed record ServerOptions(bool OnlineMode = true, bool NetworkAccess = false, bool WhiteList = false, int MaxPlayers = 20, string Motd = "Starlight server");
public sealed record ServerLaunch(int JavaMajor, string[] Arguments, string Source, string Build);
public sealed record SocialLogin(string Name, string Password);
public sealed record SocialSession(string Id, string Name, string Token);
public sealed record Friend(string Id, string Name, string Status, string? Room, bool Pending = false, bool Outgoing = false);
public sealed record SocialUser(string Id, string Name);
public sealed record ChatMessage(string From, string Name, string Target, string Text, DateTimeOffset At);
public sealed record Presence(string Status, string? Room = null);
public sealed record ServiceInfo(string Service, string Version, string? MicrosoftClientId, int NatPort, string UpdateFeedPath, bool CurseForgeAvailable);
public sealed record DeviceLogin(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);
public sealed record PackProject(string Source, string Id, string Name, string Description, string Author, long Downloads, string Website)
{
    public string Subtitle => $"{Author}  ·  {Downloads:N0} скачиваний";
}
public sealed record PackVersion(string Id, string Name, string Minecraft, string Loader, string Url, string? Hash, string Algorithm)
{
    public override string ToString() => $"{Name}  ·  {Minecraft}  ·  {Loader}";
}
public sealed record PackPage(IReadOnlyList<PackProject> Items, int Total, int Offset);
public sealed record PackOrigin(string Source, string ProjectId, string VersionId, string Name);
public interface IModProvider
{
    string Name { get; }
    Task<IReadOnlyList<ModProject>> SearchAsync(string query, Instance instance, CancellationToken ct);
    Task<ModFile> ResolveAsync(string projectId, string? versionId, Instance instance, CancellationToken ct);
}
