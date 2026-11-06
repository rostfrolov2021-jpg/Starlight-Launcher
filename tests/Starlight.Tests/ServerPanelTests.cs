using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Starlight.Domain;
using Starlight.Infrastructure;

static class ServerPanelTests
{
    public static async Task<int> RunAsync(string root)
    {
        var passed = 0;
        void Check(bool good, string name) { if (!good) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
        var file = Path.Combine(root, "panel.properties");
        await File.WriteAllTextAsync(file, "# world settings\nlevel-name=precious-world\nonline-mode=true\nonline-mode=false\nserver-ip=127.0.0.1\n");
        await ServerProperties.WriteAsync(file, new(false, true, true, 12, "My server"), 25567, default);
        var settings = ServerProperties.Read(file);
        Check(!settings.OnlineMode && settings.NetworkAccess && settings.WhiteList && settings.MaxPlayers == 12, "server control options roundtrip");
        var text = await File.ReadAllTextAsync(file);
        Check(text.Contains("level-name=precious-world") && text.Split("online-mode=").Length == 2 && File.Exists(file + ".starlight.bak"), "server settings preserve world fields, deduplicate keys and keep backup");
        await ServerProperties.WriteAsync(file, new(), 19132, default, true);
        Check(ServerProperties.Read(file, true).OnlineMode && File.ReadAllText(file).Contains("xbox-auth=true"), "Nukkit uses Xbox authentication property");
        Check(ServerProperties.Command(" /list ") == "list", "console accepts slash-prefixed Minecraft command");
        foreach (var command in new[] { "", "say test\nstop", "list\0" })
        {
            try { ServerProperties.Command(command); throw new Exception("Invalid command accepted"); } catch (ArgumentException) { Check(true, "console rejects empty/control/multiline input"); }
        }
        try { ServerProperties.Player("Steve\nstop"); throw new Exception("Invalid player accepted"); } catch (ArgumentException) { Check(true, "operator shortcut validates player name"); }
        using var response = JsonDocument.Parse("{\"error\":\"unauthorized_client\",\"error_description\":\"AADSTS700016 details must not be leaked\"}");
        var message = Authentication.MicrosoftError(response.RootElement).Message;
        Check(message.Contains("Application (client) ID") && message.Contains("AADSTS700016") && !message.Contains("must not"), "Microsoft errors explain incorrect app ID without raw auth details");
        using var http = new HttpClient(new RejectHandler());
        await using var social = new SocialClient(http);
        try { await social.ConnectAsync("http://localhost:5088", "Player", "short", true, default); throw new Exception("Weak registration input accepted"); } catch (ArgumentException e) { Check(e.Message.Contains("12"), "registration validates password before network"); }
        try { await social.ConnectAsync("http://localhost:5088", "Player", "long_test_password", true, default); throw new Exception("Duplicate registration accepted"); } catch (InvalidOperationException e) { Check(e.Message == "Это имя уже занято.", "registration displays server error instead of generic HTTP status"); }
        Check(ApiClient.ServiceUri("http://localhost:5088").Host == "127.0.0.1", "local service explicitly uses reachable IPv4 loopback");
        return passed;
    }
    public static async Task CoreAsync(string[] args)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightLauncher/1.2");
        var downloads = new Downloads(http); var cores = new ServerCores(downloads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25)); var ct = timeout.Token;
        if (args[0] == "--core-metadata")
        {
            foreach (var kind in new[] { ServerKind.Spigot, ServerKind.Forge, ServerKind.Nukkit, ServerKind.Arclight, ServerKind.Youer })
            {
                var item = await cores.ResolveAsync(kind, "1.21.1", ct);
                if (!item.Url.StartsWith("https://")) throw new Exception("Bad core source");
                Console.WriteLine($"PASS official {kind} metadata: {item.Build}");
            }
            return;
        }
        var selected = Enum.Parse<ServerKind>(args[1]); var version = args[2];
        var root = Path.GetFullPath(Path.Combine("artifacts", "core-smoke", selected.ToString()));
        var paths = new PathService(root); var java = new JavaService(paths, downloads); var game = new GameService(paths, downloads, java, new(paths));
        var serverService = new ServerService(paths, downloads, game, java);
        var progress = new Progress<TransferProgress>(p => { if (p.Message.Contains("ERROR") || p.Message.Contains("Success") || p.Message.Contains("Downloading")) Console.WriteLine(p.Message); });
        var server = (await serverService.ListAsync(ct)).FirstOrDefault(s => s.Version == (selected == ServerKind.Nukkit ? "Bedrock" : version)) ?? await serverService.CreateAsync("Core smoke", version, selected, true, selected == ServerKind.Nukkit ? 25966 : 25965, progress, ct);
        Console.WriteLine("PASS created " + selected);
        if (args.Contains("--install-only")) return;
        await ServerProperties.WriteAsync(Path.Combine(paths.Server(server.Id), "server.properties"), new(false), server.Port, ct, selected == ServerKind.Nukkit);
        var ready = new TaskCompletionSource(); var listed = new TaskCompletionSource(); var op = new TaskCompletionSource();
        using var process = await serverService.StartAsync(server, progress, line => {
            Console.WriteLine(line);
            if (line.Contains("Done (")) ready.TrySetResult();
            if (line.Contains("players online", StringComparison.OrdinalIgnoreCase) || line.Contains("Connected players", StringComparison.OrdinalIgnoreCase)) listed.TrySetResult();
            if (line.Contains("operator", StringComparison.OrdinalIgnoreCase) && line.Contains("ConsoleTest", StringComparison.OrdinalIgnoreCase)) op.TrySetResult();
        }, ct);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(8), ct);
            await ServerService.SendCommandAsync(process, "list", ct); await listed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (selected != ServerKind.Nukkit) { await ServerService.SendCommandAsync(process, "deop ConsoleTest", ct); await ServerService.SendCommandAsync(process, "op ConsoleTest", ct); await op.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); await ServerService.SendCommandAsync(process, "deop ConsoleTest", ct); }
            await ServerService.StopAsync(process, ct);
            if (process.ExitCode != 0) throw new Exception("Server shutdown failed");
            Console.WriteLine("PASS " + selected + " start, stdin commands and graceful stop");
        }
        finally { if (!process.HasExited) { try { await ServerService.StopAsync(process, CancellationToken.None); } finally { if (!process.HasExited) process.Kill(true); } } }
    }
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"Это имя уже занято.\"}", Encoding.UTF8, "application/json") });
    }
}
