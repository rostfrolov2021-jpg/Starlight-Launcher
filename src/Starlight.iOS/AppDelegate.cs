using Avalonia;
using Avalonia.iOS;
using Foundation;
using Starlight.Mobile;
using UIKit;

namespace Starlight.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Platform = new IosPlatform();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
