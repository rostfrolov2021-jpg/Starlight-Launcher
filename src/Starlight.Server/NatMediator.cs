using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;

namespace Starlight.Server;

public sealed class NatMediator(IConfiguration config, ILogger<NatMediator> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> allowed = new();
    public void Allow(string token) => allowed[token] = DateTimeOffset.UtcNow.AddMinutes(2);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new EventBasedNetListener(); var nat = new EventBasedNatPunchListener();
        var manager = new NetManager(listener) { NatPunchEnabled = true };
        var waiting = new Dictionary<string, (IPEndPoint Local, IPEndPoint Remote)>();
        nat.NatIntroductionRequest += (local, remote, token) => {
            if (!allowed.TryGetValue(token, out var expires) || expires < DateTimeOffset.UtcNow) return;
            if (waiting.TryGetValue(token, out var peer) && !peer.Remote.Equals(remote))
            {
                manager.NatPunchModule.NatIntroduce(peer.Local, peer.Remote, local, remote, token); waiting.Remove(token);
            }
            else waiting[token] = (local, remote);
        };
        listener.ConnectionRequestEvent += request => request.Reject();
        manager.NatPunchModule.Init(nat);
        var port = int.TryParse(config["STARLIGHT_NAT_PORT"], out var value) ? value : 5089;
        if (!manager.Start(port)) { log.LogError("Could not bind NAT UDP port {Port}", port); return; }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                manager.PollEvents(); manager.NatPunchModule.PollEvents();
                foreach (var item in allowed.Where(x => x.Value < DateTimeOffset.UtcNow)) { allowed.TryRemove(item.Key, out _); waiting.Remove(item.Key); }
                await Task.Delay(15, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { manager.Stop(); }
    }
}
