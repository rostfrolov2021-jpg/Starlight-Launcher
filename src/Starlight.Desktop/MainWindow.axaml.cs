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
        model.ConfirmMultiplayer = async (title, text) => {
            var dialog = new Window { Title = title, Width = 560, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var cancel = new Button { Content = "Отмена" }; var confirm = new Button { Content = "Продолжить" };
            cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0,16,0,0), Children = { cancel, confirm } };
            DockPanel.SetDock(buttons, Dock.Bottom);
            dialog.Content = new DockPanel { Margin = new Thickness(24), Children = { buttons, new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } } } };
            return await dialog.ShowDialog<bool>(this);
        };
        model.ConfirmServerDelete = async server => {
            var dialog = new Window { Title = "Удалить сервер?", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var cancel = new Button { Content = "Отмена" };
            var confirm = new Button { Content = "Удалить из списка" };
            cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
                new TextBlock { Text = "Удалить «" + server.Name + "»?", FontSize = 20, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Мир, плагины и настройки сохранятся в архиве. Удаление можно отменить кнопкой в списке серверов.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } }
            } };
            return await dialog.ShowDialog<bool>(this);
        };
        model.PickPackFile = async () => {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Импорт сборки", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Сборки Minecraft") { Patterns = ["*.mrpack", "*.zip"] }] });
            return files.FirstOrDefault()?.TryGetLocalPath();
        };
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Page)) DispatcherTimer.RunOnce(() => this.FindControl<ScrollViewer>("MainScroll")!.Offset = default, TimeSpan.FromMilliseconds(100)); };
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
