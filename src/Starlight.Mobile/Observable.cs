using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Starlight.Mobile;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

/// <summary>Async command that ignores re-entrancy (a double tap on a phone must not start two installs) and surfaces errors.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception> onError) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running;
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (running) return;
        running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { onError(e); }
        finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public void Execute(object? parameter) => execute();
}
