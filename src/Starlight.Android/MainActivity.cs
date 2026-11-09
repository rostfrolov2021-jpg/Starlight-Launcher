using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Starlight.Mobile;

[assembly: Application(Label = "Starlight", LargeHeap = true, HardwareAccelerated = true)]

namespace Starlight.Droid;

[Activity(Label = "Starlight", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Platform = new AndroidPlatform(this);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
