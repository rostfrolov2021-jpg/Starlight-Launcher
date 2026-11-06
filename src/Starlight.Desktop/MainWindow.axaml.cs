using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Starlight.Desktop;
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    private bool closing;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this); DataContext = model;
        model.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        model.CloseForUpdate = () => Dispatcher.UIThread.Post(Close);
        model.PickPackFile = async () => {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Импорт сборки", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Сборки Minecraft") { Patterns = ["*.mrpack", "*.zip"] }] });
            return files.FirstOrDefault()?.TryGetLocalPath();
        };
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Page)) this.FindControl<ScrollViewer>("MainScroll")!.Offset = default; };
        Opened += async (_, _) => {
            await model.InitializeAsync();
            if (Environment.GetEnvironmentVariable("STARLIGHT_SMOKE_WORKFLOW") == "1")
            {
                async Task Run(AsyncCommand command) { command.Execute(null); while (!command.CanExecute(null)) await Task.Delay(100); }
                await Run(model.StartLocalService);
                var connected = model.ServiceStatus.StartsWith("Подключено:", StringComparison.Ordinal);
                model.PackQuery = "Simply Optimized"; await Run(model.FindPacks);
                model.SelectedPack = model.Packs.FirstOrDefault(); await Run(model.LoadPackVersions);
                await File.WriteAllTextAsync(Path.Combine(model.RootPath, "workflow-result.json"), System.Text.Json.JsonSerializer.Serialize(new { connected, packs = model.Packs.Count, versions = model.PackVersions.Count, model.Status }));
            }
            if (Environment.GetEnvironmentVariable("STARLIGHT_SCREENSHOT_DIR") is { Length: > 0 } screenshotDirectory)
            {
                Directory.CreateDirectory(screenshotDirectory);
                for (var page = 0; page < 9; page++)
                {
                    model.Page = page; await Task.Delay(250);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                    bitmap.Render(this); bitmap.Save(Path.Combine(screenshotDirectory, $"page-{page}.png"));
                }
                model.Page = 0;
            }
            if (Environment.GetEnvironmentVariable("STARLIGHT_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                DispatcherTimer.RunOnce(() => {
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                    bitmap.Render(this); bitmap.Save(screenshot);
                }, TimeSpan.FromSeconds(1));
            }
            if (Environment.GetEnvironmentVariable("STARLIGHT_SMOKE_EXIT") == "1")
                DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(2));
        };
        Closing += async (_, args) => {
            if (closing) return;
            args.Cancel = true; closing = true;
            try { await model.DisposeAsync(); } finally { Close(); }
        };
    }
}
