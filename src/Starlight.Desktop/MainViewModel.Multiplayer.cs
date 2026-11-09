using Starlight.Domain;
using Starlight.Infrastructure;

namespace Starlight.Desktop;

public sealed partial class MainViewModel
{
    private PublishedFriendPack? publishedPack;
    public Func<string, string, Task<bool>>? ConfirmMultiplayer { get; set; }
    public AsyncCommand StopSharing { get; private set; } = null!;
    private async Task StopSharingAsync(CancellationToken ct)
    {
        try { await social.StopHostingAsync(ct); }
        finally { if (publishedPack != null) { await publishedPack.DisposeAsync(); publishedPack = null; } }
        SocialStatus = "Приглашение закрыто";
    }
    private async Task ShareServerAsync(CancellationToken ct)
    {
        social.RequireHub();
        if (serverProcess is not { HasExited: false } || runningServer == null) throw new InvalidOperationException("Сначала запустите сервер.");
        if (runningServer.Kind == ServerKind.Nukkit) throw new InvalidOperationException("Приглашения поддерживают Minecraft Java. Для Nukkit нужен Bedrock-клиент и прямой UDP-доступ.");
        var instance = RequireInstance();
        if (instance.MinecraftVersion != runningServer.Version) throw new InvalidOperationException("Выберите клиентскую сборку с версией Minecraft " + runningServer.Version);
        var required = runningServer.Kind switch { ServerKind.Fabric => ModLoader.Fabric, ServerKind.Forge or ServerKind.Arclight => ModLoader.Forge, ServerKind.Youer => ModLoader.NeoForge, _ => (ModLoader?)null };
        if (required != null && instance.Loader != required) throw new InvalidOperationException("Для этого сервера выберите клиентскую сборку " + required);
        var packs = new FriendPacks(paths); var files = packs.Files(instance);
        if (ConfirmMultiplayer == null || !await ConfirmMultiplayer("Открыть игру друзьям?", "Сборка: " + instance.Name + " · Minecraft " + instance.MinecraftVersion + " · " + instance.Loader + "\n\nДрузья смогут скачать перечисленные файлы и подключиться к запущенному серверу. Проверьте, что конфигурации не содержат личных ключей и что вы можете распространять эти файлы. Миры и плагины сервера не передаются.\n\n" + (files.Length == 0 ? "Дополнительных файлов нет." : string.Join("\n", files)))) return;
        Status = "Подготовка сборки для друзей…";
        var snapshot = await packs.PublishAsync(instance, files, ct);
        try { await StopSharingAsync(ct); await social.HostAsync(runningServer.Port, runningServer.Name, ct, snapshot); publishedPack = snapshot; }
        catch { await snapshot.DisposeAsync(); throw; }
        SocialStatus = "Игра открыта друзьям · " + instance.Name;
    }
    private async Task JoinFriendAsync(CancellationToken ct)
    {
        try { await JoinFriendCoreAsync(ct); }
        catch { await social.CloseGuestConnectionAsync(); throw; }
    }
    private async Task JoinFriendCoreAsync(CancellationToken ct)
    {
        EnsureGameStopped();
        if (SelectedAccount == null) throw new InvalidOperationException("Сначала выберите игровой аккаунт.");
        var friend = RequireFriend(); var room = friend.Room ?? throw new InvalidOperationException("Друг не публикует сервер.");
        var pack = await social.GetRoomPackAsync(room, ct);
        if (pack != null)
        {
            FriendPacks.Validate(pack);
            if (ConfirmMultiplayer == null || !await ConfirmMultiplayer("Сборка друга", $"{friend.Name} предлагает «{pack.Name}»\nMinecraft {pack.Minecraft} · {pack.Loader} {pack.LoaderVersion}\n{pack.Files} файлов · {pack.Bytes / 1048576d:F1} МБ\n\nСборка загрузится в отдельную папку и запустится с нужной версией игры. Моды и скрипты выполняют код на компьютере — продолжайте только если доверяете другу и сборке.")) return;
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct); transfer.CancelAfter(TimeSpan.FromMinutes(30));
            var downloadPort = await social.JoinAsync(room, transfer.Token, true);
            var installed = await new FriendPacks(paths).ReceiveAsync(pack, downloadPort, settings.MemoryMb, Report(), transfer.Token);
            Instances.Add(installed); SelectedInstance = installed;
        }
        var port = await social.JoinAsync(room, ct); await LaunchAsync(ct, port);
    }
}
