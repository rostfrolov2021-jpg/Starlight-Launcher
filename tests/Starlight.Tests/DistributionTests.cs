using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Starlight.Domain;
using Starlight.Infrastructure;

static class DistributionTests
{
    public static async Task<int> RunAsync(string root)
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
        async Task Reject<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL " + name); }
        var paths = new PathService(Path.Combine(root, "distribution"));
        Check(UpdateChannels.Migrate("") == UpdateChannels.OfficialFeed && new LauncherSettings().UpdateFeedUrl == UpdateChannels.OfficialFeed, "new installations use public GitHub feed");
        Check(UpdateChannels.Migrate("http://localhost:5088/api/updates/latest") == UpdateChannels.OfficialFeed && UpdateChannels.Migrate("http://127.0.0.1:5088/api/updates/latest/") == UpdateChannels.OfficialFeed, "legacy local feed migrates to GitHub");
        Check(UpdateChannels.Migrate("https://custom.example/releases/latest.json") == "https://custom.example/releases/latest.json", "custom remote update feed is preserved");
        using var rsa = RSA.Create(2048);
        var release = new ReleaseManifest("9.1.0", "test", DateTimeOffset.UtcNow, [new("win-x64", "https://example.test/release.zip", new string('a', 64), 123)]);
        var payload = JsonSerializer.SerializeToUtf8Bytes(release, JsonStore.Options);
        var signed = new SignedRelease(Convert.ToBase64String(payload), Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        Check(ReleaseTrust.Verify(signed, rsa.ExportSubjectPublicKeyInfoPem()).Version == "9.1.0", "signed release verifies");
        await Reject<CryptographicException>(() => Task.Run(() => ReleaseTrust.Verify(signed)), "untrusted signing key rejected");
        payload[^2] ^= 1;
        await Reject<CryptographicException>(() => Task.Run(() => ReleaseTrust.Verify(signed with { Payload = Convert.ToBase64String(payload) }, rsa.ExportSubjectPublicKeyInfoPem())), "tampered update manifest rejected");
        int calls = 0;
        using var retryHttp = new HttpClient(new Handler(_ => { calls++; var result = Json(calls == 1 ? "{}" : "{\"ok\":true}", calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK); result.Headers.RetryAfter = new(TimeSpan.Zero); return result; }));
        Check((await new ApiClient(retryHttp).GetAsync("https://example.test/api", default)).GetProperty("ok").GetBoolean() && calls == 2, "API 429 retries with Retry-After");
        await Reject<ArgumentException>(() => Task.Run(() => ApiClient.ServiceUri("http://remote.example")), "remote API requires HTTPS");
        foreach (var path in new[] { "../outside", "a/../../bad", "C:/bad", "a/./bad", "a/b.", "/absolute" })
            await Reject<InvalidDataException>(() => Task.Run(() => SafeArchive.Child(paths.Root, path)), "archive rejects " + path);
        using var fileHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        var catalog = new ModpackCatalog(new ApiClient(fileHttp), () => "", () => null);
        var installer = new ModpackInstaller(paths, new Downloads(fileHttp), catalog);
        var archive = Path.Combine(paths.Cache, "sample.mrpack");
        void Pack(string location, string overridePath, bool badHash = false)
        {
            using var zip = ZipFile.Open(location, ZipArchiveMode.Create);
            var index = new { formatVersion = 1, game = "minecraft", name = "Imported", dependencies = new Dictionary<string,string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.16.0" }, files = new object[] {
                new { path = "mods/example.jar", hashes = new { sha512 = badHash ? new string('0', 128) : Convert.ToHexString(SHA512.HashData([1,2,3])) }, downloads = new[] { "https://example.test/mod.jar" } },
                new { path = "mods/server.jar", hashes = new { sha512 = new string('0',128) }, downloads = new[] { "https://example.test/server.jar" }, env = new { client = "unsupported" } }
            } };
            using (var output = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open())) output.Write(JsonSerializer.Serialize(index));
            using (var output = new StreamWriter(zip.CreateEntry(overridePath).Open())) output.Write("config");
            using (var output = new StreamWriter(zip.CreateEntry("client-overrides/options.txt").Open())) output.Write("client-settings");
        }
        Pack(archive, "overrides/config/example.txt");
        var installed = await installer.ImportAsync(archive, "", 2048, new Progress<TransferProgress>(), default);
        Check(installed.Loader == ModLoader.Fabric && installed.LoaderVersion == "0.16.0" && installed.Name == "Imported", "mrpack pins Minecraft and loader versions");
        Check(File.Exists(Path.Combine(paths.Game(installed.Id), "config/example.txt")) && File.ReadAllText(Path.Combine(paths.Game(installed.Id), "options.txt")) == "client-settings", "mrpack applies common and client overrides");
        Check(ModService.Scan(paths.Mods(installed.Id)).Count == 1, "mrpack skips server-only files and checks hashes");
        var broken = Path.Combine(paths.Cache, "broken.mrpack"); Pack(broken, "overrides/config/a", true);
        await Reject<IOException>(() => installer.ImportAsync(broken, "Broken", 2048, new Progress<TransferProgress>(), default), "failed pack download rejected");
        Check((await new InstanceService(paths).ListAsync()).Count == 1 && Directory.GetDirectories(Path.Combine(paths.Cache, "pack-staging")).Length == 0, "failed import leaves library intact and removes staging");
        var traversal = Path.Combine(paths.Cache, "traversal.mrpack"); Pack(traversal, "overrides/../../../outside");
        await Reject<InvalidDataException>(() => installer.ImportAsync(traversal, "Bad", 2048, new Progress<TransferProgress>(), default), "traversal in pack override rejected before commit");
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) { var e = zip.CreateEntry("overrides/link"); e.ExternalAttributes = unchecked((int)0xA1FF0000); }
            memory.Position = 0; using var read = new ZipArchive(memory, ZipArchiveMode.Read);
            await Reject<InvalidDataException>(() => SafeArchive.ExtractAsync(read, "overrides/", paths.Root, default), "archive symlinks rejected");
        }
        int polls = 0; DeviceLogin? shown = null;
        using var authHttp = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch {
            var p when p.EndsWith("devicecode") => Json("{\"user_code\":\"ABCD1234\",\"device_code\":\"private-device-code\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"expires_in\":60,\"interval\":1}"),
            var p when p.EndsWith("/token") => ++polls == 1 ? Json("{\"error\":\"authorization_pending\"}", HttpStatusCode.BadRequest) : Json("{\"access_token\":\"ms-token\",\"refresh_token\":\"refresh\"}"),
            "/user/authenticate" => Json("{\"Token\":\"xbox-token\"}"),
            "/xsts/authorize" => Json("{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"hash\",\"xid\":\"xuid\"}]}}"),
            "/authentication/login_with_xbox" => Json("{\"access_token\":\"minecraft-token\",\"expires_in\":86400}"),
            "/minecraft/profile" => Json("{\"name\":\"TestPlayer\",\"id\":\"uuid\",\"skins\":[]}"),
            _ => throw new Exception("Unexpected auth endpoint")
        }));
        var auth = new Authentication(authHttp);
        var account = await auth.LoginWithCodeAsync(Guid.NewGuid().ToString(), code => shown = code, default);
        Check(shown?.UserCode == "ABCD1234" && polls == 2 && account.IsMicrosoft && account.Xuid == "xuid" && account.RefreshToken == "refresh", "device-code pending to Microsoft/Xbox/Minecraft profile flow");
        await Reject<InvalidOperationException>(() => auth.LoginWithCodeAsync("", _ => {}, default), "missing publisher Client ID reports actionable error");
        using var cancel = new CancellationTokenSource();
        await Reject<OperationCanceledException>(() => auth.LoginWithCodeAsync(Guid.NewGuid().ToString(), _ => cancel.Cancel(), cancel.Token), "device login is cancellable");
        using var curseHttp = new HttpClient(new Handler(request => {
            Check(request.RequestUri!.Host == "service.example" && request.RequestUri.AbsolutePath == "/api/curseforge/mods/search" && !request.Headers.Contains("x-api-key"), "CurseForge proxy uses linked service without exposing key");
            return Json("{\"data\":[],\"pagination\":{\"totalCount\":0}}");
        }));
        Check((await new ModpackCatalog(new ApiClient(curseHttp), () => "", () => "https://service.example").SearchAsync("CurseForge", "test", "", "Все", 0, "Популярные", default)).Total == 0, "CurseForge pack search parses empty page");
        return count;
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
