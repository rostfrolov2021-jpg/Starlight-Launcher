using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Concentus.Enums;
using Concentus.Structs;
using LiteNetLib;

namespace Starlight.Infrastructure;

// One caller, one receiver. Authenticated Opus frames; audio hardware opens only after explicit user action.
public sealed class VoiceService : IAsyncDisposable
{
    private CancellationTokenSource? stop;
    private Task? worker;
    public bool Muted { get; set; }
    public bool Deafened { get; set; }
    public double Volume { get; set; } = 0.8;
    public event Action<string>? Status;
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public async Task StartAsync(string mediatorHost, int port, string secret, bool initiator, CancellationToken ct)
    {
        await DisposeAsync(); ct.ThrowIfCancellationRequested();
        var key = Convert.FromHexString(secret); if (key.Length != 32) throw new ArgumentException("Некорректный ключ голоса.");
        stop = new CancellationTokenSource(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Run(() => RunAsync(mediatorHost, port, key, initiator, ready, stop.Token));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    private async Task RunAsync(string host, int port, byte[] key, bool initiator, TaskCompletionSource ready, CancellationToken ct)
    {
        uint capture = 0, playback = 0; NetManager? manager = null;
        try
        {
            if (Sdl.Init(0x10) != 0) throw new IOException("Не удалось инициализировать аудио SDL.");
            var spec = new Sdl.AudioSpec { Frequency = 48000, Format = 0x8010, Channels = 1, Samples = 960 };
            capture = Sdl.Open(null, 1, ref spec, out _, 0); playback = Sdl.Open(null, 0, ref spec, out _, 0);
            if (capture == 0 || playback == 0) throw new IOException("Микрофон или устройство воспроизведения недоступны.");
            var encoder = OpusEncoder.Create(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP); encoder.Bitrate = 24000; encoder.UseInbandFEC = true;
            var decoder = OpusDecoder.Create(48000, 1);
            var listener = new EventBasedNetListener(); var nat = new EventBasedNatPunchListener();
            manager = new NetManager(listener) { NatPunchEnabled = true, AutoRecycle = true };
            var token = Convert.ToHexString(SHA256.HashData(key)); NetPeer? peer = null;
            using var aes = new AesGcm(key, 16); ulong outgoing = 0, incoming = 0;
            listener.ConnectionRequestEvent += request => { if (peer == null) request.AcceptIfKey(token); else request.Reject(); };
            listener.PeerConnectedEvent += connected => { peer = connected; Status?.Invoke("Голос: прямое соединение"); };
            listener.PeerDisconnectedEvent += (disconnected, _) => { if (disconnected == peer) { peer = null; Status?.Invoke("Голос: соединение потеряно"); } };
            nat.NatIntroductionSuccess += (endpoint, _, received) => { if (received == token && initiator && peer == null) manager.Connect(endpoint, token); };
            listener.NetworkReceiveEvent += (_, reader, _, _) => {
                var packet = reader.GetRemainingBytes(); if (packet.Length is < 29 or > 1500) return;
                var direction = packet[0]; if (direction != (initiator ? 2 : 1)) return;
                var sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(4, 8)); if (sequence <= incoming) return;
                var plain = new byte[packet.Length - 28];
                try { aes.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(28), packet.AsSpan(12, 16), plain); } catch (CryptographicException) { return; }
                incoming = sequence;
                if (Deafened) { Sdl.Clear(playback); return; }
                var pcm = new short[5760]; var count = decoder.Decode(plain, 0, plain.Length, pcm, 0, pcm.Length, false);
                for (var i = 0; i < count; i++) pcm[i] = (short)(pcm[i] * Math.Clamp(Volume, 0, 1));
                if (Sdl.Queued(playback) > 9600) Sdl.Clear(playback);
                var bytes = new byte[count * 2]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length); Sdl.Queue(playback, bytes, (uint)bytes.Length);
            };
            manager.NatPunchModule.Init(nat); if (!manager.Start()) throw new IOException("Не удалось открыть UDP-сокет.");
            Sdl.Pause(capture, 0); Sdl.Pause(playback, 0); ready.TrySetResult();
            var nextPunch = DateTimeOffset.MinValue; var started = DateTimeOffset.UtcNow;
            var raw = new byte[1920]; var pcmInput = new short[960]; var compressed = new byte[1275];
            while (!ct.IsCancellationRequested)
            {
                manager.PollEvents(); manager.NatPunchModule.PollEvents();
                if (peer == null && DateTimeOffset.UtcNow > nextPunch)
                {
                    manager.NatPunchModule.SendNatIntroduceRequest(host, port, token); nextPunch = DateTimeOffset.UtcNow.AddSeconds(2);
                    Status?.Invoke(DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15) ? "Голос: NAT блокирует прямое UDP-соединение" : "Голос: устанавливается соединение…");
                }
                while (Sdl.Queued(capture) >= raw.Length)
                {
                    Sdl.Dequeue(capture, raw, (uint)raw.Length);
                    if (Muted || peer == null) continue;
                    Buffer.BlockCopy(raw, 0, pcmInput, 0, raw.Length);
                    var length = encoder.Encode(pcmInput, 0, 960, compressed, 0, compressed.Length);
                    var packet = new byte[length + 28]; packet[0] = (byte)(initiator ? 1 : 2); BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(4, 8), ++outgoing);
                    aes.Encrypt(packet.AsSpan(0, 12), compressed.AsSpan(0, length), packet.AsSpan(28), packet.AsSpan(12, 16)); peer.Send(packet, DeliveryMethod.Sequenced);
                }
                await Task.Delay(5, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ready.TrySetCanceled(ct); }
        catch (Exception e) { ready.TrySetException(e); Status?.Invoke("Голос: " + e.Message); }
        finally { manager?.Stop(); if (capture != 0) Sdl.Close(capture); if (playback != 0) Sdl.Close(playback); CryptographicOperations.ZeroMemory(key); }
    }
    public async ValueTask DisposeAsync()
    {
        if (stop != null) { await stop.CancelAsync(); if (worker != null) await worker; stop.Dispose(); stop = null; worker = null; }
    }
}

internal static class Sdl
{
    static Sdl()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, (name, assembly, path) => {
            if (name != "starlight_sdl") return IntPtr.Zero;
            foreach (var candidate in new[] { "SDL2", "SDL2.dll", "libSDL2-2.0.so.0", "libSDL2.dylib" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var library)) return library;
            return IntPtr.Zero;
        });
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AudioSpec { public int Frequency; public ushort Format; public byte Channels; public byte Silence; public ushort Samples; public ushort Padding; public uint Size; public IntPtr Callback; public IntPtr Userdata; }
    [DllImport("starlight_sdl", EntryPoint = "SDL_InitSubSystem", CallingConvention = CallingConvention.Cdecl)] internal static extern int Init(uint flags);
    [DllImport("starlight_sdl", EntryPoint = "SDL_OpenAudioDevice", CallingConvention = CallingConvention.Cdecl)] internal static extern uint Open([MarshalAs(UnmanagedType.LPUTF8Str)] string? device, int capture, ref AudioSpec desired, out AudioSpec obtained, int changes);
    [DllImport("starlight_sdl", EntryPoint = "SDL_PauseAudioDevice", CallingConvention = CallingConvention.Cdecl)] internal static extern void Pause(uint device, int pause);
    [DllImport("starlight_sdl", EntryPoint = "SDL_CloseAudioDevice", CallingConvention = CallingConvention.Cdecl)] internal static extern void Close(uint device);
    [DllImport("starlight_sdl", EntryPoint = "SDL_GetQueuedAudioSize", CallingConvention = CallingConvention.Cdecl)] internal static extern uint Queued(uint device);
    [DllImport("starlight_sdl", EntryPoint = "SDL_ClearQueuedAudio", CallingConvention = CallingConvention.Cdecl)] internal static extern void Clear(uint device);
    [DllImport("starlight_sdl", EntryPoint = "SDL_QueueAudio", CallingConvention = CallingConvention.Cdecl)] internal static extern int Queue(uint device, byte[] data, uint length);
    [DllImport("starlight_sdl", EntryPoint = "SDL_DequeueAudio", CallingConvention = CallingConvention.Cdecl)] internal static extern uint Dequeue(uint device, byte[] data, uint length);
}
