using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Starlight.Domain;
using Starlight.Infrastructure;
using Starlight.Platform;

namespace Starlight.Mobile;

public sealed record InstanceRow(Instance Model)
{
    public string Title => $"{Model.Icon}  {Model.Name}";
    public string Subtitle => $"{Model.MinecraftVersion} · {Model.Loader} · {Model.MemoryMb} МБ";
}

public sealed record ResultRow(ModProject Project)
{
    public string Title => Project.Name;
    public string Subtitle => Project.Description;
}

public sealed record LocalRow(LocalMod Item)
{
    public string Label => Item.Label;
}

/// <summary>Java adapter: bundled JRE on Android, Adoptium download elsewhere.</summary>
internal sealed class PlatformJava(IPlatformServices platform) : IJavaProvider
{
    public Task<string> EnsureAsync(int major, IProgress<TransferProgress>? progress, CancellationToken ct, bool jdk = false)
        => platform.EnsureJavaAsync(major, progress, ct);
}

public sealed class MobileViewModel : Observable
{
    private readonly IPlatformServices platform;
    private readonly PathService paths;
    private readonly InstanceService instances;
    private readonly GameService game;
    private readonly ModService mods;
    private readonly ModrinthProvider modrinth;
    private readonly ResourceCatalog resources;
    private readonly ModpackInstaller packs;
    private readonly Authentication auth;
    private readonly DispatcherTimer perfTimer;
    private readonly StringBuilder logBuffer = new();
    private CancellationTokenSource? operation;
    private Account? microsoftAccount;

    public MobileViewModel(IPlatformServices platform)
    {
        this.platform = platform;
        paths = new PathService(platform.DataRoot);
        var http = new HttpClient(); http.DefaultRequestHeaders.UserAgent.ParseAdd("StarlightLauncher-Mobile/1.0");
        var downloads = new Downloads(http);
        instances = new InstanceService(paths);
        game = new GameService(paths, downloads, new PlatformJava(platform), instances) { Tuning = MobileTuning.For(platform.Device.TotalRamMb) };
        mods = new ModService(paths, downloads);
        modrinth = new ModrinthProvider(downloads);
        resources = new ResourceCatalog(paths, downloads);
        packs = new ModpackInstaller(paths, downloads, new ModpackCatalog(new ApiClient(http), () => "", () => null));
        auth = new Authentication(http);

        MaxMemoryMb = MobileTuning.MaxHeapMb(platform.Device.TotalRamMb);
        JavaOptions = ["Авто", .. platform.JavaMajors.Select(x => x.ToString())];
        javaChoice = JavaOptions[0];
        LoaderOptions = Enum.GetNames<ModLoader>();
        newLoader = nameof(ModLoader.Fabric);
        memory = Math.Min(2048, MaxMemoryMb);

        Navigate = new RelayCommand<string>(p => Page = int.Parse(p ?? "0"));
        Play = new AsyncRelayCommand(PlayAsync, Fail);
        Stop = new RelayCommand(() => operation?.Cancel());
        Create = new AsyncRelayCommand(CreateAsync, Fail);
        SaveSettings = new AsyncRelayCommand(SaveSettingsAsync, Fail);
        Search = new AsyncRelayCommand(SearchAsync, Fail);
        Install = new AsyncRelayCommand(InstallAsync, Fail);
        RemoveLocal = new AsyncRelayCommand(RemoveLocalAsync, Fail);
        ImportPack = new AsyncRelayCommand(ImportPackAsync, Fail);
        MicrosoftLogin = new AsyncRelayCommand(MicrosoftLoginAsync, Fail);

        perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        perfTimer.Tick += (_, _) => SamplePerformance();
        perfTimer.Start();
    }

    // ----- layout -----------------------------------------------------------------
    private bool isCompact = true;
    /// <summary>True on phones (&lt; 720 dp wide): bottom navigation, single column. False on tablets / landscape: side rail.</summary>
    public bool IsCompact { get => isCompact; set { if (Set(ref isCompact, value)) Raise(nameof(IsWide)); } }
    public bool IsWide => !isCompact;

    private int page;
    public int Page
    {
        get => page;
        set { if (Set(ref page, value)) { Raise(nameof(IsInstancesPage)); Raise(nameof(IsModsPage)); Raise(nameof(IsSettingsPage)); } }
    }
    public bool IsInstancesPage => page == 0;
    public bool IsModsPage => page == 1;
    public bool IsSettingsPage => page == 2;

    public ICommand Navigate { get; }
    public ICommand Play { get; }
    public ICommand Stop { get; }
    public ICommand Create { get; }
    public ICommand SaveSettings { get; }
    public ICommand Search { get; }
    public ICommand Install { get; }
    public ICommand RemoveLocal { get; }
    public ICommand ImportPack { get; }
    public ICommand MicrosoftLogin { get; }

    /// <summary>Set by the view: opens the OS file picker (SAF on Android, UIDocumentPicker on iOS) and returns a readable local copy.</summary>
    public Func<Task<string?>>? PickPackFile { get; set; }

    // ----- status -----------------------------------------------------------------
    private string status = "Готово";
    public string Status { get => status; set => Set(ref status, value); }
    private double percent;
    public double Percent { get => percent; set => Set(ref percent, value); }
    private bool busy;
    public bool Busy { get => busy; set => Set(ref busy, value); }
    private string logText = "";
    public string LogText { get => logText; set => Set(ref logText, value); }
    public string DeviceLine => $"{platform.Device.Platform} · {platform.Device.TotalRamMb / 1024.0:0.#} ГБ RAM · {platform.Device.CpuCores} ядер";
    public bool CanPlay => platform.Device.CanLaunchGame;
    public string? LaunchBlocker => platform.Device.LaunchBlocker;
    public bool HasLaunchBlocker => !platform.Device.CanLaunchGame;

    // ----- instances --------------------------------------------------------------
    public ObservableCollection<InstanceRow> Instances { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public string[] LoaderOptions { get; }

    private InstanceRow? selected;
    public InstanceRow? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            if (value != null)
            {
                Memory = Math.Min(value.Model.MemoryMb, MaxMemoryMb);
                JvmArgs = value.Model.JvmArgs ?? "";
                JavaChoice = value.Model.JavaMajor?.ToString() is { } j && JavaOptions.Contains(j) ? j : JavaOptions[0];
            }
            Raise(nameof(HasSelection)); RefreshLocal();
        }
    }
    public bool HasSelection => selected != null;

    private string newName = "Моя сборка";
    public string NewName { get => newName; set => Set(ref newName, value); }
    private string? newVersion;
    public string? NewVersion { get => newVersion; set => Set(ref newVersion, value); }
    private string newLoader;
    public string NewLoader { get => newLoader; set => Set(ref newLoader, value); }
    private string nickname = "Player";
    public string Nickname { get => nickname; set => Set(ref nickname, value); }

    // ----- settings (per instance) -------------------------------------------------
    public int MaxMemoryMb { get; }
    public string[] JavaOptions { get; }
    private double memory;
    /// <summary>Heap in MB. The slider snaps to 256 MB steps.</summary>
    public double Memory
    {
        get => memory;
        set { if (Set(ref memory, value)) { Raise(nameof(MemoryText)); Raise(nameof(MemoryWarning)); Raise(nameof(HasMemoryWarning)); } }
    }
    public string MemoryText => $"{(int)memory} МБ ({memory / 1024:0.0} ГБ)";
    public bool HasMemoryWarning => memory > platform.Device.TotalRamMb * 0.45;
    public string MemoryWarning => "Выделено больше 45% памяти устройства — Android может закрыть игру. Уменьшите значение или дальность прорисовки.";
    private string javaChoice;
    public string JavaChoice { get => javaChoice; set => Set(ref javaChoice, value); }
    private string jvmArgs = "";
    public string JvmArgs { get => jvmArgs; set => Set(ref jvmArgs, value); }

    private string microsoftClientId = "";
    public string MicrosoftClientId { get => microsoftClientId; set => Set(ref microsoftClientId, value); }

    // ----- performance overlay ----------------------------------------------------
    private bool showOverlay = true;
    public bool ShowOverlay { get => showOverlay; set => Set(ref showOverlay, value); }
    private string perfText = "";
    public string PerfText { get => perfText; private set => Set(ref perfText, value); }

    private void SamplePerformance()
    {
        if (!showOverlay) return;
        try
        {
            var s = platform.Performance.Read();
            static string T(double? v) => v is { } x ? $"{x:0}°C" : "—";
            var fps = s.Fps is { } f ? $"{f:0}" : "—";
            PerfText = $"FPS {fps}   CPU {T(s.CpuTempC)}   GPU {T(s.GpuTempC)}"
                + (s.Thermal != null ? $"   {s.Thermal}" : "")
                + $"\nRAM {s.RamUsedMb}/{s.RamTotalMb} МБ   Лаунчер {s.ProcessMb} МБ";
        }
        catch (Exception e) { PerfText = "Мониторинг недоступен: " + e.Message; }
    }

    // ----- mods / resource packs / shaders ----------------------------------------
    public string[] ResourceTabs { get; } = ["Моды", "Текстурпаки", "Шейдеры"];
    private int resourceTab;
    public int ResourceTab { get => resourceTab; set { if (Set(ref resourceTab, value)) { Results.Clear(); RefreshLocal(); } } }
    private string query = "";
    public string Query { get => query; set => Set(ref query, value); }
    public ObservableCollection<ResultRow> Results { get; } = [];
    public ObservableCollection<LocalRow> Local { get; } = [];
    private ResultRow? selectedResult;
    public ResultRow? SelectedResult { get => selectedResult; set => Set(ref selectedResult, value); }
    private LocalRow? selectedLocal;
    public LocalRow? SelectedLocal { get => selectedLocal; set => Set(ref selectedLocal, value); }

    private string LocalDirectory(Instance i) => resourceTab switch
    {
        0 => paths.Mods(i.Id),
        1 => resources.Directory(i, ResourceKind.ResourcePack),
        _ => resources.Directory(i, ResourceKind.Shader)
    };

    private void RefreshLocal()
    {
        Local.Clear();
        if (selected == null) return;
        var items = resourceTab == 0 ? ModService.Scan(LocalDirectory(selected.Model)) : ResourceCatalog.Scan(LocalDirectory(selected.Model));
        foreach (var item in items) Local.Add(new LocalRow(item));
    }

    // ----- operations -------------------------------------------------------------
    public async Task InitializeAsync()
    {
        try
        {
            foreach (var i in await instances.ListAsync()) Instances.Add(new InstanceRow(i));
            Selected = Instances.FirstOrDefault();
            var all = await game.VersionsAsync(CancellationToken.None);
            foreach (var v in all.Where(v => v.Type == "release").Take(60)) Versions.Add(v.Id);
            NewVersion = Versions.FirstOrDefault();
        }
        catch (Exception e) { Fail(e); }
    }

    private void Fail(Exception e) { Status = e.Message; Busy = false; Log("ОШИБКА: " + e.Message); }

    private void Log(string line)
    {
        void Append()
        {
            logBuffer.AppendLine(line);
            if (logBuffer.Length > 20_000) logBuffer.Remove(0, logBuffer.Length - 15_000); // bounded: a chatty JVM must not grow memory forever
            LogText = logBuffer.ToString();
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }

    private IProgress<TransferProgress> Progress() => new Progress<TransferProgress>(p => { Status = p.Message; Percent = p.Percent; });

    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        operation?.Dispose(); operation = new CancellationTokenSource();
        Busy = true; Percent = 0;
        try { await work(operation.Token); }
        finally { Busy = false; }
    }

    private async Task CreateAsync() => await RunOperationAsync(async ct =>
    {
        var item = await instances.CreateAsync(NewName, NewVersion ?? "", Enum.Parse<ModLoader>(NewLoader), (int)Math.Min(memory, 2048), "✦", ct);
        var row = new InstanceRow(item); Instances.Add(row); Selected = row; Status = "Сборка создана";
    });

    private async Task SaveSettingsAsync()
    {
        if (selected == null) return;
        var updated = selected.Model with
        {
            MemoryMb = (int)memory,
            JvmArgs = string.IsNullOrWhiteSpace(jvmArgs) ? null : jvmArgs.Trim(),
            JavaMajor = int.TryParse(javaChoice, out var major) ? major : null
        };
        await instances.SaveAsync(updated);
        var index = Instances.IndexOf(selected);
        var row = new InstanceRow(updated); Instances[index] = row; Selected = row;
        Status = "Настройки сохранены";
    }

    private async Task PlayAsync() => await RunOperationAsync(async ct =>
    {
        if (selected == null) throw new InvalidOperationException("Выберите сборку.");
        if (platform.Runtime is not { } runtime) throw new InvalidOperationException(platform.Device.LaunchBlocker ?? "Запуск игры недоступен на этом устройстве.");
        var account = microsoftAccount ?? Authentication.Offline(nickname.Trim());
        Status = "Подготовка…";
        var plan = await game.PrepareLaunchAsync(selected.Model, account, Progress(), ct);
        Status = "Игра запущена"; Percent = 100;
        var code = await runtime.RunAsync(plan, Log, ct);
        Status = code == 0 ? "Игра закрыта" : $"Игра завершилась с кодом {code}";
    });

    private async Task SearchAsync() => await RunOperationAsync(async ct =>
    {
        if (selected == null) throw new InvalidOperationException("Выберите сборку.");
        var instance = selected.Model;
        var found = resourceTab switch
        {
            0 => await modrinth.SearchAsync(query, instance, ct),
            1 => await resources.SearchAsync(ResourceKind.ResourcePack, query, instance, ct),
            _ => await resources.SearchAsync(ResourceKind.Shader, query, instance, ct)
        };
        Results.Clear(); foreach (var p in found) Results.Add(new ResultRow(p));
        Status = $"Найдено: {Results.Count}";
    });

    private async Task InstallAsync() => await RunOperationAsync(async ct =>
    {
        if (selected == null || selectedResult == null) throw new InvalidOperationException("Выберите сборку и результат поиска.");
        var instance = selected.Model; var project = selectedResult.Project;
        switch (resourceTab)
        {
            case 0: await mods.InstallAsync(modrinth, project, instance, Progress(), ct); break;
            case 1: await resources.InstallAsync(ResourceKind.ResourcePack, project, instance, Progress(), ct); break;
            default: await resources.InstallAsync(ResourceKind.Shader, project, instance, Progress(), ct); break;
        }
        RefreshLocal(); Status = $"Установлено: {project.Name}";
    });

    private Task RemoveLocalAsync()
    {
        if (selectedLocal == null) return Task.CompletedTask;
        if (resourceTab == 0) ModService.Delete(selectedLocal.Item, paths.Cache); else ResourceCatalog.Delete(selectedLocal.Item);
        RefreshLocal(); Status = "Удалено";
        return Task.CompletedTask;
    }

    private async Task ImportPackAsync() => await RunOperationAsync(async ct =>
    {
        var file = PickPackFile != null ? await PickPackFile() : null;
        if (file == null) return;
        try
        {
            var item = await packs.ImportAsync(file, Path.GetFileNameWithoutExtension(file), (int)Math.Min(memory, 2048), Progress(), ct);
            var row = new InstanceRow(item); Instances.Add(row); Selected = row; Status = "Сборка импортирована";
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    });

    private async Task MicrosoftLoginAsync() => await RunOperationAsync(async ct =>
    {
        microsoftAccount = await auth.LoginWithCodeAsync(microsoftClientId.Trim(), code =>
        {
            Status = $"Откройте {code.VerificationUri} и введите код {code.UserCode}";
            _ = platform.OpenUrlAsync(code.VerificationUri);
        }, ct);
        Status = "Вход выполнен: " + microsoftAccount.Name;
    });
}

public sealed class RelayCommand<T>(Action<T?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public void Execute(object? parameter) => execute(parameter is T value ? value : default);
}
