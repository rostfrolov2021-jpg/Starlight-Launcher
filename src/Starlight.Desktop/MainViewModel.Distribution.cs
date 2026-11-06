using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using ReactiveUI;
using Starlight.Domain;
using Starlight.Infrastructure;

namespace Starlight.Desktop;

public sealed partial class MainViewModel
{
    private ModpackCatalog catalog = null!;
    private ModpackInstaller packInstaller = null!;
    private UpdateService updater = null!;
    private string? linkedService;
    private Process? serviceProcess;
    public Func<Task<string?>>? PickPackFile { get; set; }
    public Func<string, Task>? CopyText { get; set; }
    public Action? CloseForUpdate { get; set; }
    public bool IsCatalog => Page == 7;
    public bool IsUpdates => Page == 8;
    public string AppVersion => "Starlight " + UpdateService.CurrentVersion;
    public string[] PackSources { get; } = ["Modrinth", "CurseForge"];
    public string[] PackLoaders { get; } = ["Все", "Fabric", "Forge", "NeoForge"];
    public string[] PackSorts { get; } = ["Популярные", "Обновлённые", "Новые"];
    public string PackSource { get; set; } = "Modrinth";
    public string PackQuery { get; set; } = "";
    public string PackMinecraft { get; set; } = "";
    public string PackLoader { get; set; } = "Все";
    public string PackSort { get; set; } = "Популярные";
    public string PackName { get; set; } = "";
    public ObservableCollection<PackProject> Packs { get; } = [];
    public ObservableCollection<PackVersion> PackVersions { get; } = [];
    private PackProject? selectedPack;
    public PackProject? SelectedPack { get => selectedPack; set { this.RaiseAndSetIfChanged(ref selectedPack, value); PackVersions.Clear(); SelectedPackVersion = null; } }
    private PackVersion? selectedPackVersion;
    public PackVersion? SelectedPackVersion { get => selectedPackVersion; set => this.RaiseAndSetIfChanged(ref selectedPackVersion, value); }
    private int packOffset, packTotal;
    private string packSummary = "Выберите источник и нажмите «Поиск».";
    public string PackSummary { get => packSummary; set => this.RaiseAndSetIfChanged(ref packSummary, value); }
    private string serviceStatus = "Сервис ещё не привязан. Modrinth доступен без него.";
    public string ServiceStatus { get => serviceStatus; set => this.RaiseAndSetIfChanged(ref serviceStatus, value); }
    private string updateFeedUrl = "";
    public string UpdateFeedUrl { get => updateFeedUrl; set => this.RaiseAndSetIfChanged(ref updateFeedUrl, value); }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    private string updateStatus = "Обновления Starlight загружаются с GitHub Releases.";
    public string UpdateStatus { get => updateStatus; set => this.RaiseAndSetIfChanged(ref updateStatus, value); }
    private string releaseNotes = "";
    public string ReleaseNotes { get => releaseNotes; set => this.RaiseAndSetIfChanged(ref releaseNotes, value); }
    private bool updateAvailable;
    public bool UpdateAvailable { get => updateAvailable; set => this.RaiseAndSetIfChanged(ref updateAvailable, value); }
    private string userCode = "", loginUrl = "", loginHint = "Получите код и подтвердите вход на странице Microsoft.";
    public string UserCode { get => userCode; set => this.RaiseAndSetIfChanged(ref userCode, value); }
    public string LoginHint { get => loginHint; set => this.RaiseAndSetIfChanged(ref loginHint, value); }
    public bool HasLoginCode => UserCode.Length > 0;
    public AsyncCommand FindPacks { get; private set; } = null!;
    public AsyncCommand NextPacks { get; private set; } = null!;
    public AsyncCommand PreviousPacks { get; private set; } = null!;
    public AsyncCommand LoadPackVersions { get; private set; } = null!;
    public AsyncCommand InstallPack { get; private set; } = null!;
    public AsyncCommand ImportPack { get; private set; } = null!;
    public AsyncCommand OpenPackPage { get; private set; } = null!;
    public AsyncCommand LinkService { get; private set; } = null!;
    public AsyncCommand StartLocalService { get; private set; } = null!;
    public AsyncCommand StopLocalService { get; private set; } = null!;
    public AsyncCommand OpenService { get; private set; } = null!;
    public AsyncCommand OpenServiceFolder { get; private set; } = null!;
    public AsyncCommand OpenServerFolder { get; private set; } = null!;
    public AsyncCommand OpenInstanceFolder { get; private set; } = null!;
    public AsyncCommand CheckUpdates { get; private set; } = null!;
    public AsyncCommand ApplyUpdate { get; private set; } = null!;
    public AsyncCommand OpenMicrosoftPage { get; private set; } = null!;
    public AsyncCommand CopyMicrosoftCode { get; private set; } = null!;
    public AsyncCommand MicrosoftSetupHelp { get; private set; } = null!;
    private void InitializeDistribution(Downloads downloads)
    {
        catalog = new(new ApiClient(http), () => CurseForgeKey, () => linkedService);
        packInstaller = new(paths, downloads, catalog); updater = new(http, paths);
        FindPacks = Command(ct => FindPacksAsync(0, ct));
        NextPacks = Command(ct => FindPacksAsync(packOffset + 25 < packTotal ? packOffset + 25 : packOffset, ct));
        PreviousPacks = Command(ct => FindPacksAsync(Math.Max(0, packOffset - 25), ct));
        LoadPackVersions = Command(async ct => { var pack = SelectedPack ?? throw new InvalidOperationException("Выберите сборку в списке."); PackVersions.Clear(); foreach (var version in await catalog.VersionsAsync(pack, ct)) PackVersions.Add(version); SelectedPackVersion = PackVersions.FirstOrDefault(); });
        InstallPack = Command(async ct => { var item = await packInstaller.DownloadAsync(SelectedPack ?? throw new InvalidOperationException("Выберите сборку."), SelectedPackVersion ?? throw new InvalidOperationException("Нажмите «Версии» и выберите выпуск."), PackName, (int)NewMemory, Report(), ct); Instances.Add(item); SelectedInstance = item; Page = 0; });
        ImportPack = Command(async ct => { var file = PickPackFile == null ? null : await PickPackFile(); if (file == null) return; var item = await packInstaller.ImportAsync(file, PackName, (int)NewMemory, Report(), ct); Instances.Add(item); SelectedInstance = item; Page = 0; });
        OpenPackPage = Command(_ => { var address = SelectedPack?.Website ?? throw new InvalidOperationException("Выберите сборку."); if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https") Shell.Open(address); return Task.CompletedTask; });
        LinkService = Command(async ct => { await LinkServiceAsync(ct); await SaveSettingsAsync(ct); });
        StartLocalService = Command(StartServiceAsync);
        StopLocalService = Command(async _ => { if (serviceProcess is { HasExited: false }) { serviceProcess.Kill(true); await serviceProcess.WaitForExitAsync(); } serviceProcess?.Dispose(); serviceProcess = null; ServiceStatus = "Локальный сервис остановлен."; });
        OpenService = Command(_ => { Shell.Open(ApiClient.ServiceUri(SocialUrl).AbsoluteUri); return Task.CompletedTask; });
        OpenServiceFolder = Command(_ => { var folder = Path.Combine(paths.Root, "service"); Directory.CreateDirectory(folder); Shell.Open(folder); return Task.CompletedTask; });
        OpenServerFolder = Command(_ => { Shell.Open(paths.Server((SelectedServer ?? throw new InvalidOperationException("Выберите сервер.")).Id)); return Task.CompletedTask; });
        OpenInstanceFolder = Command(_ => { Shell.Open(paths.Game(RequireInstance().Id)); return Task.CompletedTask; });
        CheckUpdates = Command(CheckUpdatesAsync);
        ApplyUpdate = Command(async ct => {
            EnsureGameStopped(); if (serverProcess is { HasExited: false }) throw new InvalidOperationException("Перед обновлением остановите игровой сервер.");
            await CheckUpdatesAsync(ct); if (!UpdateAvailable) return;
            var request = await updater.PrepareAsync(AppContext.BaseDirectory, Report(), ct);
            await SaveSettingsAsync(ct); UpdateService.StartHelper(request); CloseForUpdate?.Invoke();
        });
        // These two actions must remain usable while the authorization polling occupies the action gate.
        OpenMicrosoftPage = new(() => { if (HasLoginCode) Shell.Open(loginUrl); return Task.CompletedTask; });
        CopyMicrosoftCode = new(async () => { if (CopyText != null && HasLoginCode) await CopyText(UserCode); });
        MicrosoftSetupHelp = new(() => { Shell.Open("https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app"); return Task.CompletedTask; });
    }
    private async Task FindPacksAsync(int offset, CancellationToken ct)
    {
        var result = await catalog.SearchAsync(PackSource, PackQuery, PackMinecraft, PackLoader, offset, PackSort, ct);
        Packs.Clear(); SelectedPack = null; foreach (var item in result.Items) Packs.Add(item);
        packOffset = result.Offset; packTotal = result.Total;
        PackSummary = result.Total == 0 ? "Сборки не найдены. Попробуйте убрать фильтры." : $"{offset + 1}–{offset + result.Items.Count} из {result.Total:N0}";
    }
    private async Task LinkServiceAsync(CancellationToken ct)
    {
        var info = await new ApiClient(http).ConnectAsync(SocialUrl, ct);
        linkedService = ApiClient.ServiceUri(SocialUrl).AbsoluteUri; SocialUrl = linkedService; NatPort = info.NatPort;
        if (!string.IsNullOrWhiteSpace(info.MicrosoftClientId)) MicrosoftClientId = info.MicrosoftClientId;
        // Resolve only same-service relative feed paths from configuration.
        var feed = new Uri(new Uri(linkedService), info.UpdateFeedPath);
        if (feed.Authority != new Uri(linkedService).Authority || feed.Scheme != new Uri(linkedService).Scheme) throw new InvalidDataException("Некорректная ссылка обновлений сервиса.");
        // Chat/API linking must not replace the public release channel with localhost.
        UpdateFeedUrl = UpdateChannels.Migrate(UpdateFeedUrl);
        ServiceStatus = $"Подключено: {linkedService}\nMicrosoft: {(string.IsNullOrWhiteSpace(MicrosoftClientId) ? "владелец ещё не настроил вход" : "настроен")}. CurseForge: {(info.CurseForgeAvailable ? "ключ на сервере" : "нужен ключ владельца")}.";
        foreach (var property in new[] { nameof(SocialUrl), nameof(MicrosoftClientId), nameof(NatPort) }) this.RaisePropertyChanged(property);
    }
    private async Task StartServiceAsync(CancellationToken ct)
    {
        // Reuse a compatible service already started by another Starlight window.
        if (serviceProcess is not { HasExited: false })
        {
            try { using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct); probe.CancelAfter(TimeSpan.FromSeconds(2)); await new ApiClient(http).ConnectAsync("http://127.0.0.1:5088", probe.Token); SocialUrl = "http://127.0.0.1:5088"; await LinkServiceAsync(ct); await SaveSettingsAsync(ct); return; }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException) { }
        }
        if (serviceProcess is not { HasExited: false })
        {
            var file = Path.Combine(AppContext.BaseDirectory, "service", OperatingSystem.IsWindows() ? "Starlight.Server.exe" : "Starlight.Server");
            if (!File.Exists(file)) throw new FileNotFoundException("Локальный сервис доступен в собранном выпуске лаунчера.");
            var folder = Path.Combine(paths.Root, "service"); Directory.CreateDirectory(folder);
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5088";
            start.Environment["STARLIGHT_DATA"] = Path.Combine(folder, "users.json");
            start.Environment["STARLIGHT_RELEASES"] = Path.Combine(paths.Root, "releases");
            start.Environment["STARLIGHT_MICROSOFT_CLIENT_ID"] = MicrosoftClientId;
            start.Environment["STARLIGHT_CURSEFORGE_KEY"] = CurseForgeKey;
            serviceProcess?.Dispose(); serviceProcess = Process.Start(start);
        }
        SocialUrl = "http://127.0.0.1:5088"; this.RaisePropertyChanged(nameof(SocialUrl));
        await LinkServiceAsync(ct); await SaveSettingsAsync(ct);
    }
    private async Task EnsureSocialServiceAsync(CancellationToken ct)
    {
        var uri = ApiClient.ServiceUri(SocialUrl);
        if (!uri.IsLoopback || uri.Port != 5088) return;
        try { using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct); probe.CancelAfter(TimeSpan.FromSeconds(2)); await new ApiClient(http).ConnectAsync(uri.AbsoluteUri, probe.Token); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException) { await StartServiceAsync(ct); }
    }
    private async Task CheckUpdatesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl)) throw new InvalidOperationException("Сначала привяжите сервис в настройках или укажите URL ленты.");
        UpdateAvailable = false;
        try { var release = await updater.CheckAsync(UpdateFeedUrl, ct); UpdateAvailable = release != null; ReleaseNotes = release?.Notes ?? ""; UpdateStatus = release == null ? "Установлена актуальная версия " + UpdateService.CurrentVersion : "Доступна версия " + release.Version; }
        catch (Exception e) when (e is not OperationCanceledException) { UpdateStatus = "Лента пока недоступна: " + e.Message; throw; }
    }
    private async Task LoadDistributionAsync(CancellationToken ct)
    {
        UpdateFeedUrl = UpdateChannels.Migrate(settings.UpdateFeedUrl); CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        await DiscoverOfficialSocialAsync(false, ct);
        if (settings.ServiceLinked) { try { await EnsureSocialServiceAsync(ct); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5)); await LinkServiceAsync(timeout.Token); } catch (Exception e) when (!ct.IsCancellationRequested) { ServiceStatus = "Сервис недоступен: " + e.Message; linkedService = settings.SocialUrl; } }
        if (CheckUpdatesOnStartup && UpdateFeedUrl.Length > 0) { try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5)); await CheckUpdatesAsync(timeout.Token); } catch (Exception) when (!ct.IsCancellationRequested) { UpdateStatus = "Не удалось проверить обновления. Проверку можно повторить вручную."; } }
    }
    private async Task LoginMicrosoftAsync(CancellationToken ct)
    {
        UserCode = ""; this.RaisePropertyChanged(nameof(HasLoginCode));
        try {
            await SaveSettingsAsync(ct);
            var account = await auth.LoginWithCodeAsync(MicrosoftClientId, code => Dispatcher.UIThread.Post(() => {
                UserCode = code.UserCode; loginUrl = code.VerificationUri; LoginHint = $"Откройте Microsoft и введите код. Он действует до {code.ExpiresAt.ToLocalTime():HH:mm}. Лаунчер дождётся подтверждения."; this.RaisePropertyChanged(nameof(HasLoginCode));
            }), ct);
            AddAccount(account); if (vaultUnlocked) await vault.SaveAsync(Accounts, VaultPassword, ct);
            LoginHint = "Вход выполнен: " + account.Name + (vaultUnlocked ? ". Аккаунт сохранён." : ". Сохраните аккаунт в защищённом хранилище ниже.");
        } catch { LoginHint = "Вход не завершён. Причина указана в строке состояния; можно получить новый код."; throw; }
        finally { UserCode = ""; this.RaisePropertyChanged(nameof(HasLoginCode)); }
    }
}
