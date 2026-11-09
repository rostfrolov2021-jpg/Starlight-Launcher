using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Starlight.Mobile;

public sealed partial class MainView : UserControl
{
    /// <summary>dp width below which the phone layout (bottom navigation) is used.</summary>
    private const double CompactBreakpoint = 720;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is MobileViewModel vm) vm.IsCompact = e.NewSize.Width < CompactBreakpoint;
    }

    private void Attach()
    {
        if (DataContext is not MobileViewModel vm) return;
        vm.IsCompact = Bounds.Width < CompactBreakpoint;
        vm.PickPackFile = PickPackAsync;
    }

    // Avalonia's StorageProvider is Storage Access Framework on Android and UIDocumentPicker on iOS: no extra permissions needed.
    private async Task<string?> PickPackAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Сборка (.mrpack / .zip)", AllowMultiple = false });
        if (files.Count == 0) return null;
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(files[0].Name));
        await using var input = await files[0].OpenReadAsync();
        await using var output = File.Create(temp);
        await input.CopyToAsync(output); // streamed, never buffered whole
        return temp;
    }
}
