using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using Starlight.Domain;
using Starlight.Infrastructure;

static class MultiplayerTests
{
    public static async Task<int> RunAsync(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var paths = new PathService(Path.Combine(root, "pack-host")); var guest = new PathService(Path.Combine(root, "pack-guest"));
        var instance = await new InstanceService(paths).CreateAsync("Snapshot", "1.20.1", ModLoader.Fabric, 2048, "", ct.Token, "0.16.10");
        var payload = RandomNumberGenerator.GetBytes(200000);
        await File.WriteAllBytesAsync(Path.Combine(paths.Mods(instance.Id), "mod.jar"), payload, ct.Token);
        foreach (var name in new[] { "config/sample.toml", "saves/world/level.dat", "plugins/private.jar", "accounts.json", "config/tool.exe" }) { var file = Path.Combine(paths.Game(instance.Id), name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, "fixture", ct.Token); }
        var packs = new FriendPacks(paths); var files = packs.Files(instance);
        Check(files.SequenceEqual(new[] { "config/sample.toml", "mods/mod.jar" }), "sharing excludes worlds, plugins, credentials and executables");
        Check(!FriendPacks.Allowed("mods/../private.jar") && !FriendPacks.Allowed("/mods/a.jar") && !FriendPacks.Allowed("mods/a.jar:secret"), "friend pack path allowlist rejects traversal and streams");
        await using var snapshot = await packs.PublishAsync(instance, files, ct.Token);
        await File.WriteAllTextAsync(Path.Combine(paths.Mods(instance.Id), "mod.jar"), "changed after invitation", ct.Token);
        var downloaded = await new FriendPacks(guest).ReceiveAsync(snapshot.Description, snapshot.Port, 2048, new Progress<TransferProgress>(), ct.Token);
        Check((await File.ReadAllBytesAsync(Path.Combine(guest.Mods(downloaded.Id), "mod.jar"), ct.Token)).SequenceEqual(payload), "immutable shared snapshot and exact binary transfer");
        Check(downloaded.Id != instance.Id && downloaded.LoaderVersion == "0.16.10" && downloaded.InstalledVersion == null, "received pack pins loader in new instance");
        try { await new FriendPacks(guest).ReceiveAsync(snapshot.Description with { Sha256 = new string('0',64) }, snapshot.Port, 2048, new Progress<TransferProgress>(), ct.Token); throw new Exception("corrupt pack accepted"); }
        catch (InvalidDataException) { Check((await new InstanceService(guest).ListAsync(ct.Token)).Count == 1, "corrupt pack rejected without altering existing instance"); }
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(guest.Cache, "friend-staging")).Any(), "failed friend transfer cleans staging");
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        var fake = Task.Run(async () => {
            var packet = await udp.ReceiveAsync(ct.Token);
            Check(packet.Buffer.Length == 20 && packet.Buffer[1] == 1, "STUN sends raw binding request without transport header");
            var reply = new byte[32]; packet.Buffer.CopyTo(reply,0); reply[0]=1; reply[3]=12; reply[21]=0x20; reply[23]=8; reply[25]=1;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)(packet.RemoteEndPoint.Port ^ 0x2112));
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(28), BinaryPrimitives.ReadUInt32BigEndian(packet.RemoteEndPoint.Address.GetAddressBytes()) ^ 0x2112A442);
            Check(StunLayer.Parse(reply, new byte[12]) == null && StunLayer.Parse(reply.AsSpan(0,31),packet.Buffer.AsSpan(8,12)) == null, "STUN rejects wrong transaction and truncated response");
            await udp.SendAsync(reply,packet.RemoteEndPoint,ct.Token);
        }, ct.Token);
        var layer = new StunLayer(); var manager = new NetManager(new EventBasedNetListener(),layer); manager.Start();
        try { var mapped = await layer.DiscoverAsync(manager,ct.Token,endpoint); await fake; Check(mapped?.Port == manager.LocalPort && mapped.Address.Equals(IPAddress.Loopback), "STUN discovers exact peer transport UDP socket"); }
        finally { manager.Stop(); }
        return count;
    }
}
