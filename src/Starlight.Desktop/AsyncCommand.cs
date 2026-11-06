using System.Windows.Input;
namespace Starlight.Desktop;
public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running;
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (running) return; running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); } finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
