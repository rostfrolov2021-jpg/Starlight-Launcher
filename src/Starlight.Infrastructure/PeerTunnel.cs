using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading.Channels;
using LiteNetLib;

namespace Starlight.Infrastructure;

// A bounded, encrypted reliable-UDP stream. Returning false means no TCP bytes were consumed;
// callers can safely fall back to the relay. A live stream is never migrated mid-packet.
public static class PeerTunnel
{
    public static async Task<bool> TryBridgeAsync(Stream tcp, string mediator, int port, string secret, bool initiator, CancellationToken ct, Action? onConnected = null)
    {
        var key = Convert.FromHexString(secret); var token = Convert.ToHexString(SHA256.HashData(key));
        var listener = new EventBasedNetListener(); var nat = new EventBasedNatPunchListener();
        var manager = new NetManager(listener) { NatPunchEnabled = true, AutoRecycle = true, DisconnectTimeout = 10000 };
        var connected = new TaskCompletionSource<NetPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var tx = new AesGcm(key, 16); using var rx = new AesGcm(key, 16);
        var packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
        using var window = new SemaphoreSlim(8, 8); ulong sent = 0, received = 0; var sendLock = new object();
        var active = false;
        void Send(NetPeer peer, byte type, ReadOnlySpan<byte> payload)
        {
            lock (sendLock)
            {
                var plain = new byte[payload.Length + 1]; plain[0] = type; payload.CopyTo(plain.AsSpan(1));
                var packet = new byte[plain.Length + 28]; packet[0] = (byte)(initiator ? 1 : 2);
                BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(4, 8), ++sent);
                tx.Encrypt(packet.AsSpan(0, 12), plain, packet.AsSpan(28), packet.AsSpan(12, 16)); peer.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }
        listener.ConnectionRequestEvent += request => { if (!connected.Task.IsCompleted) request.AcceptIfKey(token); else request.Reject(); };
        listener.PeerConnectedEvent += peer => { if (!connected.TrySetResult(peer)) peer.Disconnect(); };
        listener.PeerDisconnectedEvent += (peer, _) => { if (connected.Task.IsCompletedSuccessfully && connected.Task.Result == peer) stop.Cancel(); };
        listener.NetworkReceiveEvent += (_, reader, _, _) => {
            var packet = reader.GetRemainingBytes(); if (packet.Length is < 29 or > 32797) { stop.Cancel(); return; }
            if (packet[0] != (initiator ? 2 : 1)) return;
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(4, 8)); if (sequence <= received) return;
            var plain = new byte[packet.Length - 28];
            try { rx.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(28), packet.AsSpan(12, 16), plain); } catch (CryptographicException) { stop.Cancel(); return; }
            received = sequence;
            if (plain[0] == 1) { try { window.Release(); } catch (SemaphoreFullException) { stop.Cancel(); } }
            else if (plain[0] == 2) packets.Writer.TryComplete();
            else if (plain[0] != 0 || !packets.Writer.TryWrite(plain[1..])) stop.Cancel();
        };
        nat.NatIntroductionSuccess += (endpoint, _, value) => { if (initiator && value == token && !connected.Task.IsCompleted) manager.Connect(endpoint, token); };
        manager.NatPunchModule.Init(nat);
        if (!manager.Start()) return false;
        var poll = Task.Run(async () => {
            var next = DateTimeOffset.MinValue;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    manager.PollEvents(); manager.NatPunchModule.PollEvents();
                    if (!connected.Task.IsCompleted && DateTimeOffset.UtcNow > next) { manager.NatPunchModule.SendNatIntroduceRequest(mediator, port, token); next = DateTimeOffset.UtcNow.AddSeconds(1); }
                    await Task.Delay(5, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { stop.Cancel(); }
        }, CancellationToken.None);
        try
        {
            NetPeer peer;
            try { peer = await connected.Task.WaitAsync(TimeSpan.FromSeconds(8), ct); }
            catch (TimeoutException) { return false; }
            active = true;
            onConnected?.Invoke();
            async Task Upload()
            {
                var buffer = new byte[32768];
                while (true)
                {
                    await window.WaitAsync(stop.Token);
                    var count = await tcp.ReadAsync(buffer, stop.Token);
                    if (count == 0) { Send(peer, 2, []); break; }
                    Send(peer, 0, buffer.AsSpan(0, count));
                }
            }
            async Task Download()
            {
                await foreach (var packet in packets.Reader.ReadAllAsync(stop.Token)) { await tcp.WriteAsync(packet, stop.Token); Send(peer, 1, []); }
            }
            var up = Upload(); var down = Download(); await Task.WhenAny(up, down); await stop.CancelAsync();
            try { await Task.WhenAll(up, down); } catch (OperationCanceledException) { }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return active; }
        finally { await stop.CancelAsync(); await poll; manager.Stop(); CryptographicOperations.ZeroMemory(key); }
    }
}
