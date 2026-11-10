using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Layers;

namespace Starlight.Infrastructure;

// RFC 8489 binding on the very same UDP socket that later carries the game.
public sealed class StunLayer() : PacketLayerBase(0)
{
    private const uint Cookie = 0x2112A442;
    private readonly byte[] transaction = RandomNumberGenerator.GetBytes(12);
    private readonly TaskCompletionSource<IPEndPoint> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPEndPoint? server;
    public byte[] Request()
    {
        var data = new byte[20]; data[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), Cookie); transaction.CopyTo(data, 8); return data;
    }
    public static IPEndPoint? Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transaction)
    {
        if (data.Length < 20 || data[0] != 1 || data[1] != 1 || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != Cookie || !data.Slice(8, 12).SequenceEqual(transaction)) return null;
        var end = 20 + BinaryPrimitives.ReadUInt16BigEndian(data[2..]); if (end > data.Length) return null;
        for (var offset = 20; offset + 4 <= end;)
        {
            var kind = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]); var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]); offset += 4;
            if (offset + length > end) return null;
            if (kind == 0x20 && length == 8 && data[offset + 1] == 1)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]) ^ 0x2112;
                var address = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]) ^ Cookie;
                var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
                return port > 0 ? new IPEndPoint(new IPAddress(bytes), port) : null;
            }
            offset += (length + 3) & ~3;
        }
        return null;
    }
    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        if (server == null || !endPoint.Equals(server)) return;
        var endpoint = Parse(data.AsSpan(0, length), transaction); if (endpoint != null) result.TrySetResult(endpoint);
        length = 0;
    }
    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        // SendUnconnectedMessage adds one LiteNetLib header byte; STUN requires the raw payload.
        if (server != null && endPoint.Equals(server) && length == 21 && data.AsSpan(offset + 9, 12).SequenceEqual(transaction)) { offset++; length--; }
    }
    public async Task<IPEndPoint?> DiscoverAsync(NetManager manager, CancellationToken ct, IPEndPoint? endpoint = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (endpoint == null) { var addresses = await Dns.GetHostAddressesAsync("stun.cloudflare.com", AddressFamily.InterNetwork, timeout.Token); if (addresses.Length == 0) return null; endpoint = new(addresses[0], 3478); }
            server = endpoint;
            for (var i = 0; i < 3; i++)
            {
                manager.SendUnconnectedMessage(Request(), server);
                var ready = await Task.WhenAny(result.Task, Task.Delay(600, timeout.Token));
                if (ready == result.Task) return await result.Task;
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        return null;
    }
}
