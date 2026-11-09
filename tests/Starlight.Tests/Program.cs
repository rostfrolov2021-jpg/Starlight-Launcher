using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Concentus.Enums;
using Concentus.Structs;
using Microsoft.AspNetCore.SignalR.Client;
using Starlight.Domain;
using Starlight.Infrastructure;

if (args.FirstOrDefault() is "--core-metadata" or "--core-smoke") { await ServerPanelTests.CoreAsync(args); return; }
if (args.FirstOrDefault() == "--social-resume")
{
    using var network = new HttpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var names = (await File.ReadAllLinesAsync(args[2], timeout.Token));
    await using var alice = new SocialClient(network); await using var bob = new SocialClient(network);
    await alice.ConnectAsync(args[1], names[0], "integration_password_123", false, timeout.Token);
    await bob.ConnectAsync(args[1], names[1], "integration_password_123", false, timeout.Token);
    if (!(await alice.FriendsAsync(timeout.Token)).Any(f => f.Id == bob.Session!.Id && !f.Pending && !f.Outgoing)) throw new Exception("Friendship lost after service restart");
    var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously); bob.Message += m => received.TrySetResult(m);
    await alice.RequireHub().InvokeAsync("SendMessage", bob.Session!.Id, "after restart", timeout.Token);
    if ((await received.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)).Text != "after restart") throw new Exception("Chat unavailable after restart");
    Console.WriteLine("PASS accounts, friendship and chat survive service restart"); return;
}
if (args.FirstOrDefault() == "--update-feed")
{
    using var updateHttp = new HttpClient();
    var updatePaths = new PathService(Path.Combine(args[2], "data"));
    var update = new UpdateService(updateHttp, updatePaths);
    if ((await update.CheckAsync(args[1], default))?.Version != "9.0.0") throw new Exception("Update discovery failed");
    var requestFile = await update.PrepareAsync(Path.Combine(args[2], "app"), new Progress<TransferProgress>(), default);
    var request = (await JsonStore.ReadAsync<UpdateRequest>(requestFile))!;
    await JsonStore.WriteAsync(requestFile, request with { ParentPid = 2147483646, Restart = false });
    UpdateService.StartHelper(requestFile);
    var updaterLog = Path.Combine(Path.GetDirectoryName(requestFile)!, "updater.log");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string updateResult = "";
    while (updateResult.Length == 0) { await Task.Delay(100, deadline.Token); try { updateResult = await File.ReadAllTextAsync(updaterLog, deadline.Token); } catch (IOException) { } }
    if (!updateResult.StartsWith("Installed") || File.ReadAllText(Path.Combine(args[2], "app", "Starlight.exe")) != "new Starlight.exe") throw new Exception("Update apply failed");
    Console.WriteLine("PASS signed feed discovery, streamed download, helper staging and native update application");
    return;
}

var root = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
var paths = new PathService(root); var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
async Task Throws<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
Check(new[] { paths.Config, paths.Cache, paths.Instances, paths.Runtimes, paths.Servers, paths.Logs }.All(Directory.Exists), "isolated directories");
foreach (var filename in new[] { "../escape.jar", "..\\escape.jar", "C:bad.jar", "/absolute.jar", ".." })
    await Throws<InvalidDataException>(() => Task.Run(() => PathService.SafeChild(root, filename)), "reject path " + filename);
var instanceService = new InstanceService(paths);
var instance = await instanceService.CreateAsync("Мир / приключений", "1.20.1", ModLoader.Fabric, 2048, "✦", default);
Check(Directory.Exists(Path.Combine(paths.Game(instance.Id), "saves")), "instance isolation and unicode label");
Check((await instanceService.ListAsync()).Single() == instance, "instance metadata roundtrip");
await Throws<ArgumentException>(() => instanceService.CreateAsync("", "1.20", ModLoader.Vanilla, 2048, "", default), "reject empty instance name");
await Throws<ArgumentException>(() => Task.Run(() => Authentication.Offline("bad name")), "reject invalid nickname");
Check(Authentication.Offline("Notch").Uuid == "b50ad385829d3141a2167e7d7539ba7f", "Java-compatible offline UUID");
var offlineProfile = Authentication.Offline("Notch");
Check(GameService.CreateSession(offlineProfile).UUID == offlineProfile.Uuid && GameService.CreateSession(offlineProfile).UUID == offlineProfile.Uuid, "CmlLib launch sessions preserve offline world identity across launches");
var vault = new AccountVault(paths); var accounts = new[] { new Account("Player", "uuid", true, "secret-access", "secret-refresh") };
await vault.SaveAsync(accounts, "correct horse battery");
Check((await vault.ReadAsync("correct horse battery")).Single() == accounts[0], "encrypted vault roundtrip");
Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(paths.Config, "accounts.enc"))).Contains("secret-access"), "no plaintext token on disk");
await Throws<CryptographicException>(() => vault.ReadAsync("incorrect password"), "reject wrong vault password");
var encrypted = await File.ReadAllBytesAsync(Path.Combine(paths.Config, "accounts.enc")); encrypted[^1] ^= 1; await File.WriteAllBytesAsync(Path.Combine(paths.Config, "accounts.enc"), encrypted);
await Throws<CryptographicException>(() => vault.ReadAsync("correct horse battery"), "reject modified ciphertext");
var jar = Path.Combine(paths.Mods(instance.Id), "example.jar"); await File.WriteAllBytesAsync(jar, [1, 2, 3]);
ModService.Toggle(ModService.Scan(paths.Mods(instance.Id)).Single());
Check(File.Exists(jar + ".disabled") && !ModService.Scan(paths.Mods(instance.Id)).Single().Enabled, "disable manual JAR");
ModService.Toggle(ModService.Scan(paths.Mods(instance.Id)).Single()); Check(File.Exists(jar), "enable manual JAR");
ModService.Delete(ModService.Scan(paths.Mods(instance.Id)).Single(), paths.Cache); Check(!File.Exists(jar) && Directory.GetFiles(Path.Combine(paths.Cache, "deleted-mods")).Length == 1, "recoverable mod deletion");
using var http = new HttpClient(new FixedHandler([1, 2, 3])); var downloads = new Downloads(http); var target = Path.Combine(paths.Cache, "download.jar");
await downloads.FileAsync("https://example.test/file", target, Convert.ToHexString(SHA256.HashData([1, 2, 3])), "SHA256", null, default);
Check((await File.ReadAllBytesAsync(target)).SequenceEqual(new byte[] { 1, 2, 3 }), "verified streamed download");
await Throws<InvalidDataException>(() => downloads.FileAsync("https://example.test/file", target, new string('0', 64), "SHA256", null, default), "checksum mismatch rejected");
Check((await File.ReadAllBytesAsync(target)).SequenceEqual(new byte[] { 1, 2, 3 }) && Directory.GetFiles(paths.Cache, "*.part").Length == 0, "failed download preserves existing file and removes temporary file");
await Throws<InvalidDataException>(() => downloads.FileAsync("http://example.test/file", target, null, "SHA256", null, default), "reject cleartext artifact download");
var modService = new ModService(paths, downloads); var provider = new CyclicProvider();
await modService.InstallAsync(provider, new("Test", "a", "A", "", null), instance, new Progress<TransferProgress>(), default);
Check(ModService.Scan(paths.Mods(instance.Id)).Count == 2 && provider.Calls == 2, "cyclic required dependencies resolved once");
await modService.InstallAsync(new VersionedProvider("1"), new("Test", "versioned", "V", "", null), instance, new Progress<TransferProgress>(), default);
ModService.Toggle(ModService.Scan(paths.Mods(instance.Id)).Single(m => m.FileName == "v1.jar"));
await modService.InstallAsync(new VersionedProvider("2"), new("Test", "versioned", "V", "", null), instance, new Progress<TransferProgress>(), default);
Check(File.Exists(Path.Combine(paths.Mods(instance.Id), "v2.jar.disabled")) && !File.Exists(Path.Combine(paths.Mods(instance.Id), "v1.jar.disabled")), "mod update replaces previous version and preserves disabled state");
Directory.CreateDirectory(Path.Combine(paths.Mods(instance.Id), "v3.jar.disabled"));
await Throws<IOException>(() => modService.InstallAsync(new VersionedProvider("3"), new("Test", "versioned", "V", "", null), instance, new Progress<TransferProgress>(), default), "mod commit failure reported");
Check(File.Exists(Path.Combine(paths.Mods(instance.Id), "v2.jar.disabled")), "mod transaction rollback restores previous version");
var samples = Enumerable.Range(0, 960).Select(n => (short)(Math.Sin(n * 0.1) * 8000)).ToArray();
var encoder = OpusEncoder.Create(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP); var bytes = new byte[1275]; var length = encoder.Encode(samples, 0, 960, bytes, 0, bytes.Length);
var decoded = new short[960]; Check(OpusDecoder.Create(48000, 1).Decode(bytes, 0, length, decoded, 0, 960, false) == 960, "20ms Opus audio roundtrip");

passed += await DistributionTests.RunAsync(root);
passed += await ServerPanelTests.RunAsync(root);
passed += await FriendAndArchiveTests.RunAsync(root);
passed += await MultiplayerTests.RunAsync(root);
if (args.Contains("--online"))
{
    using var onlineHttp = new HttpClient(); onlineHttp.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightLauncher/1.0");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = timeout.Token;
    var onlineDownloads = new Downloads(onlineHttp); var java = new JavaService(paths, onlineDownloads);
    var game = new GameService(paths, onlineDownloads, java, instanceService);
    Check((await game.VersionsAsync(ct)).Any(v => v.Id == "1.20.1"), "official Mojang version manifest");
    Check(await game.JavaMajorAsync("1.17", ct) == 16, "Minecraft 1.17 requires Java 16 from metadata");
    Check(await game.JavaMajorAsync("1.20.1", ct) == 17, "Minecraft 1.20.1 Java 17 from metadata");
    Check(await game.JavaMajorAsync("1.21.1", ct) == 21, "Minecraft 1.21.1 Java 21 from metadata");
    var modrinth = new ModrinthProvider(onlineDownloads);
    Check((await modrinth.SearchAsync("sodium", instance, ct)).Count > 0, "Modrinth compatible project search");
    var sodium = await modrinth.ResolveAsync("AANobbMI", null, instance, ct);
    Check(sodium.FileName.EndsWith(".jar") && sodium.Hash.Length == 128, "Modrinth version metadata and SHA512");
    var fabric = await onlineDownloads.JsonAsync("https://meta.fabricmc.net/v2/versions/loader/1.20.1", ct);
    Check(fabric.GetArrayLength() > 0, "Fabric official loader manifest");
    var packs = new ModpackCatalog(new ApiClient(onlineHttp), () => "", () => null);
    var page = await packs.SearchAsync("Modrinth", "Simply Optimized", "1.20.1", "Fabric", 0, "Популярные", ct);
    Check(page.Items.Count > 0, "live Modrinth modpack source and filters");
    var pack = page.Items[0];
    var packVersions = await packs.VersionsAsync(pack, ct);
    Check(packVersions.Count > 0 && packVersions.All(v => v.Hash?.Length == 128), "live modpack versions and download checksums");
    if (args.Contains("--install-pack")) {
        var installedPack = await new ModpackInstaller(paths, onlineDownloads, packs).DownloadAsync(pack, packVersions[0], "Live pack smoke", 2048, new Progress<TransferProgress>(), ct);
        Check(ModService.Scan(paths.Mods(installedPack.Id)).Count > 0, "real Modrinth archive downloads and installs complete modpack");
    }
}
else if (args.Length > 0)
{
    using var network = new HttpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var ct = timeout.Token;
    var config = await new ApiClient(network).ConnectAsync(args[0], ct);
    Check(config.Service == "Starlight" && config.UpdateFeedPath == "api/updates/latest", "service link bootstrap endpoint");
    Check((await network.GetAsync(args[0] + "/api/curseforge/unknown", ct)).StatusCode == HttpStatusCode.NotFound, "catalog proxy rejects arbitrary routes");
    await using var alice = new SocialClient(network); await using var bob = new SocialClient(network); await using var stranger = new SocialClient(network);
    string? transport = null;
    alice.Notice += message => Console.WriteLine("HOST " + message); bob.Notice += message => { Console.WriteLine("GUEST " + message); if (message.StartsWith("Маршрут:")) transport = message; };
    var suffix = Guid.NewGuid().ToString("N")[..8];
    await alice.ConnectAsync(args[0], "Alice_" + suffix, "integration_password_123", true, ct);
    await bob.ConnectAsync(args[0], "Bob_" + suffix, "integration_password_123", true, ct);
    await stranger.ConnectAsync(args[0], "Other_" + suffix, "integration_password_123", true, ct);
    var found = await alice.SearchUsersAsync(" bOb ", ct);
    Check(found.Any(u => u.Id == bob.Session!.Id), "authenticated prefix search is case insensitive and trims whitespace");
    Check((await alice.SearchUsersAsync("bo", ct)).Length == 0 && (await alice.SearchUsersAsync("Alice_" + suffix, ct)).Length == 0, "search requires three characters and excludes self");
    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bob.FriendsChanged += () => changed.TrySetResult();
    await alice.RequestFriendAsync("  " + bob.Session!.Name.ToUpperInvariant() + "  ", ct);
    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    Check((await bob.FriendsAsync(ct)).Single().Pending, "friend request arrives with live notification");
    Check((await alice.FriendsAsync(ct)).Single().Outgoing, "sender sees pending outgoing request");
    await alice.RequireHub().InvokeAsync("RequestFriend", "  " + Guid.Parse(bob.Session.Id).ToString("D").ToUpperInvariant() + "  ", ct);
    Check((await bob.FriendsAsync(ct)).Length == 1, "duplicate request is idempotent and server normalizes legacy client input");
    await alice.RequireHub().InvokeAsync("RemoveFriend", bob.Session.Id, ct);
    Check((await bob.FriendsAsync(ct)).Length == 0 && (await alice.FriendsAsync(ct)).Length == 0, "sender can cancel outgoing request on both sides");
    await bob.DisconnectAsync();
    await alice.RequestFriendAsync("Bob_" + suffix, ct);
    await bob.ConnectAsync(args[0], "Bob_" + suffix, "integration_password_123", false, ct);
    Check((await bob.FriendsAsync(ct)).Single().Pending, "offline recipient receives saved request after login");
    await bob.RequestFriendAsync(alice.Session!.Id, ct);
    await bob.RequireHub().InvokeAsync("AcceptFriend", alice.Session!.Id, ct);
    Check((await alice.FriendsAsync(ct)).Single().Id == bob.Session.Id, "friend request acceptance persisted");
    Check((await alice.FriendsAsync(ct)).All(f => !f.Pending && !f.Outgoing) && (await bob.FriendsAsync(ct)).All(f => !f.Pending && !f.Outgoing), "accepting reciprocal requests clears both pending states");
    var received = new TaskCompletionSource<ChatMessage>(); bob.Message += m => received.TrySetResult(m);
    await alice.RequireHub().InvokeAsync("SendMessage", bob.Session.Id, "hello", ct);
    Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)).Text == "hello", "SignalR direct message");
    await Throws<Microsoft.AspNetCore.SignalR.HubException>(() => stranger.RequireHub().InvokeAsync("SendMessage", bob.Session.Id, "forbidden", ct), "non-friend cannot message");
    var group = await alice.RequireHub().InvokeAsync<string>("CreateGroup", new[] { bob.Session.Id }, ct);
    await alice.RequireHub().InvokeAsync("SendMessage", group, "group message", ct);
    await Throws<Microsoft.AspNetCore.SignalR.HubException>(() => stranger.RequireHub().InvokeAsync("SendMessage", group, "forbidden", ct), "non-member cannot message group");
    using var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start(); var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
    var echoTask = Task.Run(async () => {
        try { while (!ct.IsCancellationRequested) { var client = await echo.AcceptTcpClientAsync(ct); _ = Task.Run(async () => { using (client) { var stream = client.GetStream(); var buffer = new byte[32768]; int count; while ((count = await stream.ReadAsync(buffer, ct)) > 0) await stream.WriteAsync(buffer.AsMemory(0, count), ct); } }, ct); } }
        catch (OperationCanceledException) { }
    }, ct);
    foreach (var route in new[] { "mediator", "signaling", "relay" })
    {
        if (args.Contains("--live") && route == "mediator") continue;
        var useNat = route != "relay";
        alice.EnablePeerDiscovery = bob.EnablePeerDiscovery = route == "signaling";
        alice.NatPort = route == "mediator" ? int.TryParse(Environment.GetEnvironmentVariable("STARLIGHT_TEST_NAT_PORT"), out var testNatPort) ? testNatPort : 5089 : 59999; bob.NatPort = alice.NatPort;
        var room = await alice.HostAsync(echoPort, "Echo", ct);
        await Throws<Microsoft.AspNetCore.SignalR.HubException>(() => stranger.RequireHub().InvokeAsync("OpenTunnel", room, Guid.NewGuid().ToString("N"), VoiceService.NewSecret(), ct), "non-friend cannot open tunnel");
        var port = await bob.JoinAsync(room, ct); using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var payload = RandomNumberGenerator.GetBytes(200_000); var reply = new byte[payload.Length];
        var upload = tcp.GetStream().WriteAsync(payload, ct).AsTask(); await tcp.GetStream().ReadExactlyAsync(reply, ct); await upload;
        Check(payload.SequenceEqual(reply), useNat ? "NAT tunnel binary echo 200KB" : "WebSocket fallback binary echo 200KB");
        Check(transport == (useNat ? "Маршрут: P2P" : "Маршрут: relay"), "selected transport matches expected route");
        var duringTunnel = new TaskCompletionSource<ChatMessage>(); alice.Message += m => duringTunnel.TrySetResult(m);
        await bob.RequireHub().InvokeAsync("SendMessage", alice.Session.Id, "still responsive", ct);
        Check((await duringTunnel.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)).Text == "still responsive", "chat remains responsive while tunnel is open");
        await alice.StopHostingAsync(ct);
    }
    var packs = new FriendPacks(paths); var sharedInstance = instance with { LoaderVersion = "0.16.10" };
    var sharedBytes = RandomNumberGenerator.GetBytes(200000); await File.WriteAllBytesAsync(Path.Combine(paths.Mods(instance.Id), "shared.jar"), sharedBytes, ct);
    await using (var published = await packs.PublishAsync(sharedInstance, packs.Files(sharedInstance), ct))
    {
        var room = await alice.HostAsync(echoPort, "Pack", ct, published);
        await Throws<Microsoft.AspNetCore.SignalR.HubException>(() => stranger.GetRoomPackAsync(room, ct), "stranger cannot inspect shared pack");
        await Throws<Microsoft.AspNetCore.SignalR.HubException>(() => stranger.RequireHub().InvokeAsync("OpenPackTunnel", room, Guid.NewGuid().ToString("N"), VoiceService.NewSecret(), ct), "stranger cannot download shared pack");
        var descriptor = await bob.GetRoomPackAsync(room, ct);
        Check(descriptor == published.Description, "friend receives exact pack descriptor");
        var localPort = await bob.JoinAsync(room, ct, true);
        var downloaded = await packs.ReceiveAsync(descriptor!, localPort, 2048, new Progress<TransferProgress>(), ct);
        Check(downloaded.LoaderVersion == sharedInstance.LoaderVersion && downloaded.Id != sharedInstance.Id && (await File.ReadAllBytesAsync(Path.Combine(paths.Game(downloaded.Id), "mods", "shared.jar"), ct)).SequenceEqual(sharedBytes), "friend pack travels through authenticated relay into isolated instance");
        await alice.StopHostingAsync(ct);
    }
    await timeout.CancelAsync(); echo.Stop(); await echoTask;
    if (Environment.GetEnvironmentVariable("STARLIGHT_TEST_SOCIAL_STATE") is { Length: > 0 } statePath)
        await File.WriteAllLinesAsync(statePath, [alice.Session!.Name, bob.Session!.Name]);
}
Console.WriteLine($"{passed} checks passed.");

sealed class FixedHandler(byte[] content) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
}
sealed class CyclicProvider : IModProvider
{
    public int Calls;
    public string Name => "Test";
    public Task<IReadOnlyList<ModProject>> SearchAsync(string q, Instance i, CancellationToken ct) => Task.FromResult<IReadOnlyList<ModProject>>([]);
    public Task<ModFile> ResolveAsync(string id, string? version, Instance i, CancellationToken ct) { Calls++; return Task.FromResult(new ModFile(id, "1", id + ".jar", "https://example.test/" + id, Convert.ToHexString(SHA256.HashData([1, 2, 3])), "SHA256", [new(id == "a" ? "b" : "a")])); }
}
sealed class VersionedProvider(string version) : IModProvider
{
    public string Name => "Test";
    public Task<IReadOnlyList<ModProject>> SearchAsync(string q, Instance i, CancellationToken ct) => Task.FromResult<IReadOnlyList<ModProject>>([]);
    public Task<ModFile> ResolveAsync(string id, string? requested, Instance i, CancellationToken ct) => Task.FromResult(new ModFile(id, version, "v" + version + ".jar", "https://example.test/file", Convert.ToHexString(SHA256.HashData([1, 2, 3])), "SHA256", []));
}
