using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Starlight.Platform;

namespace Starlight.Mobile;

public sealed partial class App : Application
{
    /// <summary>Set by the Android / iOS head before Avalonia starts.</summary>
    public static IPlatformServices? Platform { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var services = Platform ?? throw new InvalidOperationException("IPlatformServices не задан платформенным проектом.");
            var viewModel = new MobileViewModel(services);
            single.MainView = new MainView { DataContext = viewModel };
            _ = viewModel.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
