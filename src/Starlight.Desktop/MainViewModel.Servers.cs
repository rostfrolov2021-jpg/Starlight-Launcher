using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;
using ReactiveUI;
using Starlight.Domain;
using Starlight.Infrastructure;

namespace Starlight.Desktop;

public sealed partial class MainViewModel
{
    private readonly ConcurrentQueue<(Guid Server, string Text)> pendingServerLines = new();
    private readonly Dictionary<Guid, Queue<string>> serverHistory = new();
    private readonly SemaphoreSlim consoleGate = new(1);
    private string serverLogs = "Выберите сервер. После запуска здесь появится его вывод.";
    public string ServerLogs { get => serverLogs; set => this.RaiseAndSetIfChanged(ref serverLogs, value); }
    private string serverState = "Сервер не запущен";
    public string ServerState { get => serverState; set => this.RaiseAndSetIfChanged(ref serverState, value); }
    private string serverCommandText = "";
    public string ServerCommandText { get => serverCommandText; set => this.RaiseAndSetIfChanged(ref serverCommandText, value); }
    public string OperatorName { get; set; } = "";
    private bool serverOnlineMode = true, serverNetworkAccess, serverWhiteList;
    public bool ServerOnlineMode { get => serverOnlineMode; set => this.RaiseAndSetIfChanged(ref serverOnlineMode, value); }
    public bool ServerNetworkAccess { get => serverNetworkAccess; set { this.RaiseAndSetIfChanged(ref serverNetworkAccess, value); this.RaisePropertyChanged(nameof(ServerAddresses)); } }
    public bool ServerWhiteList { get => serverWhiteList; set => this.RaiseAndSetIfChanged(ref serverWhiteList, value); }
    private decimal serverMaxPlayers = 20, managedServerPort = 25565;
    public decimal ServerMaxPlayers { get => serverMaxPlayers; set => this.RaiseAndSetIfChanged(ref serverMaxPlayers, value); }
    public decimal ManagedServerPort { get => managedServerPort; set { this.RaiseAndSetIfChanged(ref managedServerPort, value); this.RaisePropertyChanged(nameof(ServerAddresses)); } }
    private string serverMotd = "Starlight server", serverOptionsStatus = "Изменения применяются после остановки и повторного запуска сервера.";
    public string ServerMotd { get => serverMotd; set => this.RaiseAndSetIfChanged(ref serverMotd, value); }
    public string ServerOptionsStatus { get => serverOptionsStatus; set => this.RaiseAndSetIfChanged(ref serverOptionsStatus, value); }
    public bool SelectedServerRunning => runningServer?.Id == SelectedServer?.Id && serverProcess is { HasExited: false };
    public string ServerEdition => SelectedServer?.Kind == ServerKind.Nukkit ? "Bedrock · UDP · Xbox authentication" : "Java Edition · TCP · online-mode";
    public string ServerAddresses => !ServerNetworkAccess ? $"На этом компьютере: 127.0.0.1:{ManagedServerPort:0}" : "В локальной сети: " + string.Join("  ·  ", LanAddresses().Select(ip => $"{ip}:{ManagedServerPort:0}"));
    public string CoreHint => NewServerKind switch {
        ServerKind.Nukkit => "Nukkit — Bedrock Edition, порт UDP 19132. Java-клиент к нему не подключается. Загружается текущая сборка Nukkit.",
        ServerKind.Spigot => "Spigot собирается официальным BuildTools. Загрузится JDK; сборка может занять несколько минут и требует Git.",
        ServerKind.Forge => "Forge для Java-модов. Клиенту нужны совместимые версия Forge и набор модов.",
        ServerKind.Arclight => "Arclight — гибрид модов и Bukkit-плагинов. Выбирается опубликованный Forge/NeoForge/Fabric-вариант для этой версии; новые ветки могут быть экспериментальными.",
        ServerKind.Youer => "Youer — гибрид NeoForge и Paper/Purpur API. Попробуйте Minecraft 1.21.1. Не все сочетания модов и плагинов совместимы.",
        ServerKind.Fabric => "Fabric — Java-моды. Плагины Spigot не поддерживаются.",
        ServerKind.Paper => "Paper — плагины Bukkit/Spigot. Для API укажите контактный URL в настройках.",
        _ => "Официальный сервер Minecraft Java без загрузчика модов." };
    public AsyncCommand SendServerCommand { get; private set; } = null!;
    public AsyncCommand GrantOperator { get; private set; } = null!;
    public AsyncCommand RevokeOperator { get; private set; } = null!;
    public AsyncCommand ListServerPlayers { get; private set; } = null!;
    public AsyncCommand SaveServerWorld { get; private set; } = null!;
    public AsyncCommand SaveServerOptions { get; private set; } = null!;
    public AsyncCommand ReloadServerOptions { get; private set; } = null!;
    public AsyncCommand CopyServerAddress { get; private set; } = null!;
    public AsyncCommand DeleteServer { get; private set; } = null!;
    public AsyncCommand RestoreDeletedServer { get; private set; } = null!;
    public AsyncCommand OpenDeletedServers { get; private set; } = null!;
    public Func<LocalServer, Task<bool>>? ConfirmServerDelete { get; set; }
    private string? lastDeletedServer;
    public bool CanRestoreServer => lastDeletedServer != null;
    private void InitializeServerPanel()
    {
        var archive = new ServerArchive(paths);
        DeleteServer = Command(async ct => {
            var selected = SelectedServer ?? throw new InvalidOperationException("Выберите сервер для удаления.");
            if (SelectedServerRunning) throw new InvalidOperationException("Сначала остановите выбранный сервер.");
            if (ConfirmServerDelete == null || !await ConfirmServerDelete(selected)) return;
            lastDeletedServer = await archive.ArchiveAsync(selected, ct);
            Servers.Remove(selected); SelectedServer = Servers.FirstOrDefault();
            this.RaisePropertyChanged(nameof(CanRestoreServer));
            ServerOptionsStatus = Status = "Сервер удалён из списка. Мир и настройки сохранены в архиве. Удаление можно отменить.";
        });
        RestoreDeletedServer = Command(async ct => {
            var location = lastDeletedServer ?? throw new InvalidOperationException("Нет удалённого сервера для восстановления.");
            var restored = await archive.RestoreAsync(location, ct);
            Servers.Add(restored); SelectedServer = restored; lastDeletedServer = null;
            this.RaisePropertyChanged(nameof(CanRestoreServer)); ServerOptionsStatus = Status = "Сервер восстановлен вместе с миром и настройками.";
        });
        OpenDeletedServers = Command(_ => { Directory.CreateDirectory(archive.Root); Shell.Open(archive.Root); return Task.CompletedTask; });
        SendServerCommand = new(async () => { if (await ConsoleAsync(() => ServerCommandText)) ServerCommandText = ""; });
        GrantOperator = new(async () => { await ConsoleAsync(() => "op " + ServerProperties.Player(OperatorName.Trim())); });
        RevokeOperator = new(async () => { await ConsoleAsync(() => "deop " + ServerProperties.Player(OperatorName.Trim())); });
        ListServerPlayers = new(async () => { await ConsoleAsync(() => "list"); });
        SaveServerWorld = new(async () => { await ConsoleAsync(() => "save-all"); });
        ReloadServerOptions = Command(_ => { ReadServerPanel(); return Task.CompletedTask; });
        CopyServerAddress = new(async () => { if (CopyText != null) await CopyText((ServerNetworkAccess ? LanAddresses().FirstOrDefault() ?? "127.0.0.1" : "127.0.0.1") + ":" + ManagedServerPort.ToString("0")); });
        SaveServerOptions = Command(async ct => {
            var selected = SelectedServer ?? throw new InvalidOperationException("Выберите сервер.");
            if (SelectedServerRunning) throw new InvalidOperationException("Сначала остановите этот сервер. Затем сохраните настройки и запустите его снова.");
            await ServerProperties.WriteAsync(Path.Combine(paths.Server(selected.Id), "server.properties"), new(ServerOnlineMode, ServerNetworkAccess, ServerWhiteList, (int)ServerMaxPlayers, ServerMotd), (int)ManagedServerPort, ct, selected.Kind == ServerKind.Nukkit);
            var updated = selected with { Port = (int)ManagedServerPort };
            await JsonStore.WriteAsync(Path.Combine(paths.Server(selected.Id), "server.json"), updated, ct);
            var index = Servers.IndexOf(selected); Servers[index] = updated; SelectedServer = updated;
            ServerOptionsStatus = "Настройки сохранены. Запустите сервер, чтобы применить их.";
        });
        logTimer.Tick += (_, _) => FlushServerLines();
    }
    private static string[] LanAddresses() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).Select(a => a.Address.ToString()).Distinct().ToArray();
    private async Task<bool> ConsoleAsync(Func<string> command)
    {
        if (disposed || !await consoleGate.WaitAsync(0)) return false;
        try
        {
            if (!SelectedServerRunning) throw new InvalidOperationException("Запустите выбранный сервер из Starlight. Консоль управляет процессом, запущенным этим лаунчером.");
            var value = ServerProperties.Command(command());
            await ServerService.SendCommandAsync(serverProcess!, value, lifetime.Token);
            QueueServerLine(runningServer!.Id, "> " + value); return true;
        }
        catch (Exception e) { ServerOptionsStatus = e.Message; Status = e.Message; return false; }
        finally { consoleGate.Release(); }
    }
    private void ReadServerPanel()
    {
        if (SelectedServer is not { } selected) { ServerLogs = "Выберите сервер."; ServerState = "Сервер не выбран"; this.RaisePropertyChanged(nameof(SelectedServerRunning)); return; }
        try
        {
            var options = ServerProperties.Read(Path.Combine(paths.Server(selected.Id), "server.properties"), selected.Kind == ServerKind.Nukkit);
            ServerOnlineMode = options.OnlineMode; ServerNetworkAccess = options.NetworkAccess; ServerWhiteList = options.WhiteList; ServerMaxPlayers = options.MaxPlayers; ServerMotd = options.Motd; ManagedServerPort = selected.Port;
            if (!serverHistory.ContainsKey(selected.Id))
            {
                var log = Path.Combine(paths.Server(selected.Id), "logs", "latest.log");
                serverHistory[selected.Id] = new(File.Exists(log) ? File.ReadLines(log).TakeLast(400) : []);
            }
            ServerLogs = string.Join(Environment.NewLine, serverHistory[selected.Id]);
            ServerState = SelectedServerRunning ? "Сервер работает" : "Сервер не запущен";
            ServerOptionsStatus = "Настройки меняются у остановленного сервера. Команды выполняются сразу.";
            this.RaisePropertyChanged(nameof(SelectedServerRunning)); this.RaisePropertyChanged(nameof(ServerEdition));
        }
        catch (IOException e) { ServerOptionsStatus = e.Message; }
    }
    private void QueueServerLine(Guid id, string line)
    {
        if (pendingServerLines.Count < 2000) pendingServerLines.Enqueue((id, line));
        if (line.Contains("Done (", StringComparison.Ordinal)) Dispatcher.UIThread.Post(() => { if (SelectedServer?.Id == id) ServerState = "Готов к подключению"; });
    }
    private void FlushServerLines()
    {
        bool changed = false;
        while (pendingServerLines.TryDequeue(out var item))
        {
            if (!serverHistory.TryGetValue(item.Server, out var queue)) serverHistory[item.Server] = queue = new();
            queue.Enqueue(item.Text); while (queue.Count > 500) queue.Dequeue();
            changed |= item.Server == SelectedServer?.Id;
        }
        if (changed && SelectedServer != null) ServerLogs = string.Join(Environment.NewLine, serverHistory[SelectedServer.Id]);
    }
    private async Task StartManagedServerAsync(CancellationToken ct)
    {
        if (serverProcess is { HasExited: false }) throw new InvalidOperationException("Сервер уже работает. Выберите его для команд или остановите перед запуском другого.");
        serverProcess?.Dispose(); runningServer = SelectedServer ?? throw new InvalidOperationException("Выберите сервер.");
        var id = runningServer.Id; ServerState = "Запускается… дождитесь «Готов к подключению»";
        serverProcess = await servers.StartAsync(runningServer, Report(), line => { QueueServerLine(id, line); AppendLog(line); }, ct);
        serverProcess.Exited += (_, _) => Dispatcher.UIThread.Post(() => { if (SelectedServer?.Id == id) ServerState = "Сервер остановлен"; this.RaisePropertyChanged(nameof(SelectedServerRunning)); });
        this.RaisePropertyChanged(nameof(SelectedServerRunning));
    }
}
