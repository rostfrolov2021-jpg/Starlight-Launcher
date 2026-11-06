using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using ReactiveUI;
using Serilog;
using Starlight.Domain;
using Starlight.Infrastructure;

namespace Starlight.Desktop;

public sealed partial class MainViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly PathService paths = new(Environment.GetEnvironmentVariable("STARLIGHT_HOME"));
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly InstanceService instances;
    private readonly GameService game;
    private readonly ServerService servers;
    private readonly ModService mods;
    private readonly Authentication auth;
    private readonly AccountVault vault;
    private readonly SocialClient social;
    private readonly VoiceService voice = new();
    private readonly IModProvider[] providers;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private FileSystemWatcher? watcher;
    private readonly DispatcherTimer modDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Process? gameProcess, serverProcess;
    private LocalServer? runningServer;
    private bool settingsLoaded;
    private bool disposed;
    private LauncherSettings settings = new();
    private bool vaultUnlocked;
    private string? incomingVoiceSecret;
    private readonly SemaphoreSlim actionGate = new(1);
    private readonly Queue<string> logLines = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingLogs = new();
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    public string RootPath => paths.Root;
    public ObservableCollection<Instance> Instances { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<LocalMod> Mods { get; } = [];
    public ObservableCollection<ModProject> SearchResults { get; } = [];
    public ObservableCollection<LocalServer> Servers { get; } = [];
    public ObservableCollection<Friend> Friends { get; } = [];
    public ObservableCollection<string> Groups { get; } = [];
    public ModLoader[] Loaders { get; } = Enum.GetValues<ModLoader>();
    public ServerKind[] ServerKinds { get; } = Enum.GetValues<ServerKind>();
    public string[] Sources { get; } = ["Все", "Modrinth", "CurseForge"];
    private int page;
    public int Page { get => page; set { this.RaiseAndSetIfChanged(ref page, value); this.RaisePropertyChanged(nameof(IsLibrary)); this.RaisePropertyChanged(nameof(IsMods)); this.RaisePropertyChanged(nameof(IsServers)); this.RaisePropertyChanged(nameof(IsSocial)); this.RaisePropertyChanged(nameof(IsAccounts)); this.RaisePropertyChanged(nameof(IsSettings)); this.RaisePropertyChanged(nameof(IsLogs)); this.RaisePropertyChanged(nameof(IsCatalog)); this.RaisePropertyChanged(nameof(IsUpdates)); } }
    public bool IsLibrary => Page == 0; public bool IsMods => Page == 1; public bool IsServers => Page == 2; public bool IsSocial => Page == 3; public bool IsAccounts => Page == 4; public bool IsSettings => Page == 5; public bool IsLogs => Page == 6;
    private string status = "Добро пожаловать в Starlight";
    public string Status { get => status; set => this.RaiseAndSetIfChanged(ref status, value); }
    private bool busy;
    public bool Busy { get => busy; set => this.RaiseAndSetIfChanged(ref busy, value); }
    private double progress;
    public double Progress { get => progress; set => this.RaiseAndSetIfChanged(ref progress, value); }
    private string logs = "Здесь появятся сообщения Minecraft и локального сервера.";
    public string Logs { get => logs; set => this.RaiseAndSetIfChanged(ref logs, value); }
    private Instance? selectedInstance;
    public Instance? SelectedInstance { get => selectedInstance; set { this.RaiseAndSetIfChanged(ref selectedInstance, value); this.RaisePropertyChanged(nameof(InstanceInfo)); WatchMods(); } }
    public string InstanceInfo => SelectedInstance is { } i ? $"Minecraft {i.MinecraftVersion}  /  {i.Loader}  /  {i.MemoryMb} МБ" : "Создайте первую сборку и отправляйтесь в свой мир";
    private Account? selectedAccount;
    public Account? SelectedAccount { get => selectedAccount; set => this.RaiseAndSetIfChanged(ref selectedAccount, value); }
    private LocalMod? selectedMod;
    public LocalMod? SelectedMod { get => selectedMod; set => this.RaiseAndSetIfChanged(ref selectedMod, value); }
    private ModProject? selectedResult;
    public ModProject? SelectedResult { get => selectedResult; set => this.RaiseAndSetIfChanged(ref selectedResult, value); }
    private LocalServer? selectedServer;
    public LocalServer? SelectedServer { get => selectedServer; set { this.RaiseAndSetIfChanged(ref selectedServer, value); ReadServerPanel(); } }
    private Friend? selectedFriend;
    public Friend? SelectedFriend { get => selectedFriend; set => this.RaiseAndSetIfChanged(ref selectedFriend, value); }
    private string chat = "Сообщения видны только участникам беседы и не сохраняются сервером.";
    public string Chat { get => chat; set => this.RaiseAndSetIfChanged(ref chat, value); }
    private string socialStatus = "Не подключено";
    public string SocialStatus { get => socialStatus; set => this.RaiseAndSetIfChanged(ref socialStatus, value); }
    private string voiceStatus = "Голосовой вызов не активен";
    public string VoiceStatus { get => voiceStatus; set => this.RaiseAndSetIfChanged(ref voiceStatus, value); }
    public bool Muted { get => voice.Muted; set { voice.Muted = value; this.RaisePropertyChanged(); } }
    public bool Deafened { get => voice.Deafened; set { voice.Deafened = value; this.RaisePropertyChanged(); } }
    public double Volume { get => voice.Volume; set { voice.Volume = value; this.RaisePropertyChanged(); } }
    public string NewName { get; set; } = "Мой новый мир";
    public string? NewVersion { get; set; }
    public ModLoader NewLoader { get; set; }
    public string NewIcon { get; set; } = "✦";
    public string LoaderVersion { get; set; } = "";
    public decimal NewMemory { get; set; } = 4096;
    public string Nickname { get; set; } = "Player";
    public string VaultPassword { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    public string Source { get; set; } = "Все";
    public string ServerName { get; set; } = "Мой сервер";
    private ServerKind newServerKind; public ServerKind NewServerKind { get => newServerKind; set { this.RaiseAndSetIfChanged(ref newServerKind, value); ServerPort = value == ServerKind.Nukkit ? 19132 : 25565; this.RaisePropertyChanged(nameof(ServerPort)); this.RaisePropertyChanged(nameof(CoreHint)); } }
    public decimal ServerPort { get; set; } = 25565;
    public bool AcceptedEula { get; set; }
    public string MicrosoftClientId { get; set; } = "";
    public string CurseForgeKey { get; set; } = "";
    public string SocialUrl { get; set; } = "http://localhost:5088";
    public string Contact { get; set; } = "";
    public string SocialName { get; set; } = "";
    public string SocialPassword { get; set; } = "";
    private string friendName = "";
    public string FriendName { get => friendName; set { this.RaiseAndSetIfChanged(ref friendName, value); friendSearchTimer.Stop(); FriendSearchResults.Clear(); if (value.Trim().Length >= 3) friendSearchTimer.Start(); } }
    public string MessageText { get; set; } = "";
    public string? SelectedGroup { get; set; }
    public bool SendToGroup { get; set; }
    public decimal NatPort { get; set; } = 5089;
    public AsyncCommand CreateInstance { get; }
    public AsyncCommand Launch { get; }
    public AsyncCommand AddOffline { get; }
    public AsyncCommand MicrosoftLogin { get; }
    public AsyncCommand UnlockVault { get; }
    public AsyncCommand SaveAccounts { get; }
    public AsyncCommand SearchMods { get; }
    public AsyncCommand InstallMod { get; }
    public AsyncCommand ToggleMod { get; }
    public AsyncCommand DeleteMod { get; }
    public AsyncCommand OpenMods { get; }
    public AsyncCommand CreateServer { get; }
    public AsyncCommand StartServer { get; }
    public AsyncCommand StopServer { get; }
    public AsyncCommand ShareServer { get; }
    public AsyncCommand SaveSettings { get; }
    public AsyncCommand ConnectSocial { get; }
    public AsyncCommand RegisterSocial { get; }
    public AsyncCommand AddFriend { get; }
    public AsyncCommand AcceptFriend { get; }
    public AsyncCommand RemoveFriend { get; }
    public AsyncCommand RefreshFriends { get; }
    public AsyncCommand SendMessage { get; }
    public AsyncCommand CreateGroup { get; }
    public AsyncCommand JoinFriend { get; }
    public AsyncCommand CallFriend { get; }
    public AsyncCommand AcceptCall { get; }
    public AsyncCommand EndCall { get; }
    public AsyncCommand OpenData { get; }
    public AsyncCommand RefreshVersions { get; }
    public ReactiveCommand<Unit, Unit> Cancel { get; }
    public ReactiveCommand<string, Unit> Navigate { get; }
    public MainViewModel()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightLauncher/1.1");
        var downloads = new Downloads(http); instances = new(paths); var java = new JavaService(paths, downloads);
        game = new(paths, downloads, java, instances); servers = new(paths, downloads, game, java); mods = new(paths, downloads);
        auth = new(http); vault = new(paths); social = new(http);
        InitializeDistribution(downloads); InitializeServerPanel(); InitializeSocialPanel();
        providers = [new ModrinthProvider(downloads), new CurseForgeProvider(http, () => CurseForgeKey, () => linkedService)];
        Navigate = ReactiveCommand.Create<string>(value => Page = int.Parse(value));
        Cancel = ReactiveCommand.Create(() => operation?.Cancel());
        CreateInstance = Command(async ct => { var item = await instances.CreateAsync(NewName, NewVersion ?? "", NewLoader, (int)NewMemory, NewIcon, ct, LoaderVersion); Instances.Add(item); SelectedInstance = item; });
        Launch = Command(ct => LaunchAsync(ct));
        AddOffline = Command(async ct => { var account = Authentication.Offline(Nickname.Trim()); AddAccount(account); await JsonStore.WriteAsync(Path.Combine(paths.Config, "offline.json"), Accounts.Where(a => !a.IsMicrosoft).ToList(), ct); });
        MicrosoftLogin = Command(LoginMicrosoftAsync);
        UnlockVault = Command(async ct => { var accounts = await Task.Run(() => vault.ReadAsync(VaultPassword, ct), ct); foreach (var a in accounts) AddAccount(a); vaultUnlocked = true; });
        SaveAccounts = Command(async ct => {
            if (!vaultUnlocked && File.Exists(Path.Combine(paths.Config, "accounts.enc"))) throw new InvalidOperationException("Сначала разблокируйте существующее хранилище.");
            await Task.Run(() => vault.SaveAsync(Accounts, VaultPassword, ct), ct); vaultUnlocked = true;
        });
        RefreshVersions = Command(LoadVersionsAsync);
        SearchMods = Command(SearchAsync);
        InstallMod = Command(async ct => { EnsureGameStopped(); var result = SelectedResult ?? throw new InvalidOperationException("Выберите мод."); await mods.InstallAsync(providers.First(p => p.Name == result.Source), result, RequireInstance(), Report(), ct); RefreshMods(); });
        ToggleMod = Command(_ => { EnsureGameStopped(); ModService.Toggle(SelectedMod ?? throw new InvalidOperationException("Выберите мод.")); RefreshMods(); return Task.CompletedTask; });
        DeleteMod = Command(_ => { EnsureGameStopped(); ModService.Delete(SelectedMod ?? throw new InvalidOperationException("Выберите мод."), paths.Cache); RefreshMods(); return Task.CompletedTask; });
        OpenMods = Command(_ => { Shell.Open(paths.Mods(RequireInstance().Id)); return Task.CompletedTask; });
        CreateServer = Command(async ct => { var s = await servers.CreateAsync(ServerName, NewVersion ?? "", NewServerKind, AcceptedEula, (int)ServerPort, Report(), ct); Servers.Add(s); SelectedServer = s; });
        StartServer = Command(StartManagedServerAsync);
        StopServer = Command(async ct => { if (serverProcess is { HasExited: false }) await ServerService.StopAsync(serverProcess, ct); await social.StopHostingAsync(ct); });
        ShareServer = Command(async ct => { if (serverProcess is not { HasExited: false } || runningServer == null) throw new InvalidOperationException("Сначала запустите сервер."); if (runningServer.Kind == ServerKind.Nukkit) throw new InvalidOperationException("Приглашения Starlight поддерживают Java TCP; для Nukkit используйте прямой UDP-доступ."); await social.HostAsync(runningServer.Port, runningServer.Name, ct); });
        SaveSettings = Command(SaveSettingsAsync);
        ConnectSocial = Command(ct => ConnectAsync(false, ct)); RegisterSocial = Command(ct => ConnectAsync(true, ct));
        AddFriend = SocialCommand(async ct => { await social.RequestFriendAsync(FriendName, ct); await LoadFriendsAsync(ct); FriendActionStatus = "Заявка отправлена. Друг увидит её после входа в этот же сервис."; });
        AcceptFriend = SocialCommand(async ct => { var friend = RequireFriend(); if (!friend.Pending) throw new InvalidOperationException("Выберите входящую заявку."); await social.RequireHub().InvokeAsync("AcceptFriend", friend.Id, ct); await LoadFriendsAsync(ct); FriendActionStatus = "Друг добавлен. Теперь можно общаться."; });
        RemoveFriend = SocialCommand(async ct => { await social.RequireHub().InvokeAsync("RemoveFriend", RequireFriend().Id, ct); await LoadFriendsAsync(ct); FriendActionStatus = "Друг или заявка удалены."; });
        RefreshFriends = new(RefreshFriendsSafelyAsync);
        SendMessage = SocialCommand(async ct => { var friend = SendToGroup ? null : RequireFriend(); if (friend is { Pending: true } or { Outgoing: true }) throw new InvalidOperationException("Сначала примите заявку в друзья."); var target = SendToGroup ? SelectedGroup ?? throw new InvalidOperationException("Выберите группу.") : friend!.Id; await social.RequireHub().InvokeAsync("SendMessage", target, MessageText, ct); MessageText = ""; this.RaisePropertyChanged(nameof(MessageText)); });
        CreateGroup = SocialCommand(async ct => { var id = await social.RequireHub().InvokeAsync<string>("CreateGroup", Friends.Where(f => !f.Pending && !f.Outgoing).Select(f => f.Id).ToArray(), ct); SelectedGroup = id; this.RaisePropertyChanged(nameof(SelectedGroup)); });
        JoinFriend = Command(async ct => { var room = RequireFriend().Room ?? throw new InvalidOperationException("Друг не публикует сервер."); var port = await social.JoinAsync(room, ct); await LaunchAsync(ct, port); });
        CallFriend = Command(async ct => {
            var secret = VoiceService.NewSecret(); await social.RequireHub().InvokeAsync("VoiceOffer", RequireFriend().Id, "", secret, ct);
            await voice.StartAsync(social.BaseUri!.Host, (int)NatPort, secret, true, ct);
        });
        AcceptCall = Command(async ct => { var secret = incomingVoiceSecret ?? throw new InvalidOperationException("Нет входящего вызова."); await voice.StartAsync(social.BaseUri!.Host, (int)NatPort, secret, false, ct); incomingVoiceSecret = null; });
        EndCall = Command(async _ => { await voice.DisposeAsync(); incomingVoiceSecret = null; VoiceStatus = "Вызов завершён"; });
        OpenData = Command(_ => { Shell.Open(paths.Root); return Task.CompletedTask; });
        social.Message += message => Dispatcher.UIThread.Post(() => { Chat += $"\n[{message.At.ToLocalTime():HH:mm}] {message.Name}: {message.Text}"; if (Chat.Length > 60000) Chat = Chat[^50000..]; });
        social.FriendsChanged += () => Dispatcher.UIThread.Post(() => { _ = RefreshFriendsSafelyAsync(); });
        social.GroupCreated += id => Dispatcher.UIThread.Post(() => { if (!Groups.Contains(id)) Groups.Add(id); });
        social.VoiceOffer += (from, _, secret) => Dispatcher.UIThread.Post(() => { incomingVoiceSecret = secret; VoiceStatus = "Входящий вызов от " + (Friends.FirstOrDefault(f => f.Id == from)?.Name ?? from); });
        social.Notice += text => Dispatcher.UIThread.Post(() => SocialStatus = text);
        voice.Status += text => Dispatcher.UIThread.Post(() => VoiceStatus = text);
        modDebounce.Tick += (_, _) => { modDebounce.Stop(); try { RefreshMods(); } catch (IOException e) { AppendLog(e.Message); } };
        logTimer.Tick += (_, _) => { var any = false; while (pendingLogs.TryDequeue(out var line)) { any = true; logLines.Enqueue(line); if (logLines.Count > 500) logLines.Dequeue(); } if (any) Logs = string.Join(Environment.NewLine, logLines); };
        logTimer.Start();
    }
    public async Task InitializeAsync() => await RunAsync(async ct => {
        settings = await JsonStore.ReadAsync<LauncherSettings>(Path.Combine(paths.Config, "settings.json"), ct) ?? new();
        settingsLoaded = true;
        MicrosoftClientId = settings.MicrosoftClientId; CurseForgeKey = settings.CurseForgeApiKey; SocialUrl = settings.SocialUrl; Contact = settings.Contact;
        NatPort = settings.NatPort is >= 1024 and <= 65535 ? settings.NatPort : 5089; ApplyUserAgent();
        foreach (var property in new[] { nameof(MicrosoftClientId), nameof(CurseForgeKey), nameof(SocialUrl), nameof(Contact), nameof(NatPort) }) this.RaisePropertyChanged(property);
        foreach (var i in await instances.ListAsync(ct)) Instances.Add(i);
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == settings.SelectedInstance) ?? Instances.FirstOrDefault();
        foreach (var server in await servers.ListAsync(ct)) Servers.Add(server); SelectedServer = Servers.FirstOrDefault();
        var offline = await JsonStore.ReadAsync<List<Account>>(Path.Combine(paths.Config, "offline.json"), ct) ?? [];
        foreach (var a in offline.Where(a => !a.IsMicrosoft)) AddAccount(a);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Uuid == settings.SelectedAccount) ?? Accounts.FirstOrDefault();
        await LoadDistributionAsync(ct);
        await LoadVersionsAsync(ct);
    });
    private AsyncCommand Command(Func<CancellationToken, Task> action) => new(() => RunAsync(action));
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed) return;
        if (!await actionGate.WaitAsync(0)) { Status = "Дождитесь текущей операции или отмените её."; return; }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Busy = true; Progress = 0;
        try { await action(operation.Token); Status = "Готово"; Progress = 100; }
        catch (OperationCanceledException) { Status = "Операция отменена"; }
        catch (System.Security.Cryptography.CryptographicException e) { Status = "Ошибка проверки защиты: " + e.Message; }
        catch (Exception e) { Status = e.Message; AppendLog(e.GetType().Name + ": " + e.Message); Log.Warning("Operation failed: {Type}", e.GetType().Name); }
        finally { Busy = false; operation.Dispose(); operation = null; actionGate.Release(); }
    }
    private IProgress<TransferProgress> Report() => new Progress<TransferProgress>(p => { Progress = p.Percent; Status = p.Message; });
    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        var list = await game.VersionsAsync(ct); Versions.Clear(); foreach (var v in list.Where(v => v.Type == "release")) Versions.Add(v.Id);
        NewVersion ??= Versions.FirstOrDefault(); this.RaisePropertyChanged(nameof(NewVersion));
    }
    private async Task LaunchAsync(CancellationToken ct, int? port = null)
    {
        EnsureGameStopped(); var account = SelectedAccount ?? throw new InvalidOperationException("Добавьте и выберите аккаунт.");
        account = await auth.RefreshAsync(account, MicrosoftClientId, ct); AddAccount(account);
        if (account.IsMicrosoft && vaultUnlocked) await vault.SaveAsync(Accounts, VaultPassword, ct);
        gameProcess?.Dispose(); gameProcess = await game.LaunchAsync(RequireInstance(), account, Report(), AppendLog, ct, port);
        gameProcess.Exited += (_, _) => Dispatcher.UIThread.Post(() => {
            if (social.Connected) Command(token => social.RequireHub().InvokeAsync("SetPresence", new Presence("В сети"), token)).Execute(null);
        });
        if (social.Connected) await social.RequireHub().InvokeAsync("SetPresence", new Presence("В игре"), ct);
    }
    private void AddAccount(Account account) { var existing = Accounts.FirstOrDefault(a => a.Uuid == account.Uuid); if (existing != null) Accounts.Remove(existing); Accounts.Add(account); SelectedAccount = account; }
    private Instance RequireInstance() => SelectedInstance ?? throw new InvalidOperationException("Выберите сборку.");
    private Friend RequireFriend() => SelectedFriend ?? throw new InvalidOperationException("Выберите друга.");
    private void EnsureGameStopped() { if (gameProcess is { HasExited: false }) throw new InvalidOperationException("Сначала закройте запущенный Minecraft."); }
    private void WatchMods()
    {
        watcher?.Dispose(); watcher = null; Mods.Clear(); if (SelectedInstance == null) return;
        var directory = paths.Mods(SelectedInstance.Id); Directory.CreateDirectory(directory);
        watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        FileSystemEventHandler change = (_, _) => Dispatcher.UIThread.Post(() => { modDebounce.Stop(); modDebounce.Start(); });
        watcher.Changed += change; watcher.Created += change; watcher.Deleted += change; watcher.Renamed += (_, _) => change(null!, null!);
        watcher.EnableRaisingEvents = true; RefreshMods();
    }
    private void RefreshMods() { if (SelectedInstance == null) return; Mods.Clear(); foreach (var mod in ModService.Scan(paths.Mods(SelectedInstance.Id))) Mods.Add(mod); }
    private async Task SearchAsync(CancellationToken ct)
    {
        SearchResults.Clear(); var errors = new List<string>();
        foreach (var provider in providers.Where(p => Source == "Все" || Source == p.Name))
        {
            if (provider.Name == "CurseForge" && string.IsNullOrWhiteSpace(CurseForgeKey) && linkedService == null && Source == "Все") continue;
            try { foreach (var result in await provider.SearchAsync(SearchQuery, RequireInstance(), ct)) SearchResults.Add(result); }
            catch (HttpRequestException e) { errors.Add(provider.Name + ": " + e.Message); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }
    private async Task SaveSettingsAsync(CancellationToken ct)
    {
        settings = new(MicrosoftClientId.Trim(), CurseForgeKey.Trim(), SocialUrl.Trim(), (int)NewMemory, Contact.Trim(), SelectedInstance?.Id, SelectedAccount?.Uuid, (int)NatPort, UpdateFeedUrl.Trim(), CheckUpdatesOnStartup, linkedService != null);
        await JsonStore.WriteAsync(Path.Combine(paths.Config, "settings.json"), settings, ct);
        await JsonStore.WriteAsync(Path.Combine(paths.Config, "offline.json"), Accounts.Where(a => !a.IsMicrosoft).ToList(), ct);
        ApplyUserAgent();
    }
    private void ApplyUserAgent()
    {
        http.DefaultRequestHeaders.UserAgent.Clear(); http.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightLauncher/1.1");
        if (Uri.TryCreate(Contact, UriKind.Absolute, out var uri) && uri.Scheme == "https") http.DefaultRequestHeaders.UserAgent.ParseAdd("(" + uri.AbsoluteUri + ")");
    }
    private async Task ConnectAsync(bool register, CancellationToken ct)
    {
        SocialStatus = register ? "Создаю аккаунт Starlight…" : "Подключаю аккаунт Starlight…";
        try { await EnsureSocialServiceAsync(ct); social.NatPort = (int)NatPort; await social.ConnectAsync(SocialUrl, SocialName, SocialPassword, register, ct); SocialStatus = $"Вход выполнен: {social.Session!.Name} · ID {social.Session.Id}"; await LoadFriendsAsync(ct); }
        catch (Exception e) { SocialStatus = e is OperationCanceledException ? "Подключение отменено" : e.Message; throw; }
    }
    private async Task LoadFriendsAsync(CancellationToken ct)
    {
        await friendsGate.WaitAsync(ct);
        try
        {
            var list = await social.FriendsAsync(ct); var selected = SelectedFriend?.Id;
            var oldRequests = Friends.Where(f => f.Pending).Select(f => f.Id).ToHashSet();
            Friends.Clear(); foreach (var f in list.OrderByDescending(f => f.Pending).ThenBy(f => f.Outgoing).ThenBy(f => f.Name)) Friends.Add(f);
            SelectedFriend = Friends.FirstOrDefault(f => f.Id == selected); this.RaisePropertyChanged(nameof(SelectedFriend));
            if (list.Any(f => f.Pending && !oldRequests.Contains(f.Id))) FriendActionStatus = "Есть новая заявка в друзья. Выберите её и нажмите «Принять».";
            this.RaisePropertyChanged(nameof(SocialIdentity)); this.RaisePropertyChanged(nameof(SocialScope));
        }
        finally { friendsGate.Release(); }
    }
    private void AppendLog(string line) { if (pendingLogs.Count < 2000) pendingLogs.Enqueue(line); Log.Information("{GameOutput}", line); }
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await lifetime.CancelAsync(); modDebounce.Stop(); logTimer.Stop(); friendsTimer.Stop(); friendSearchTimer.Stop(); watcher?.Dispose();
        await actionGate.WaitAsync();
        await socialActionGate.WaitAsync(); await friendsGate.WaitAsync(); await friendSearchGate.WaitAsync();
        await voice.DisposeAsync(); await social.DisposeAsync();
        if (serviceProcess is { HasExited: false }) { serviceProcess.Kill(true); await serviceProcess.WaitForExitAsync(); } serviceProcess?.Dispose();
        if (serverProcess is { HasExited: false }) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); try { await ServerService.StopAsync(serverProcess, timeout.Token); } catch (Exception e) { Log.Warning("Server shutdown: {Type}", e.GetType().Name); } }
        await consoleGate.WaitAsync(); consoleGate.Release(); if (settingsLoaded) await SaveSettingsAsync(CancellationToken.None); serverProcess?.Dispose(); gameProcess?.Dispose(); http.Dispose(); lifetime.Dispose(); actionGate.Release();
    }
}
