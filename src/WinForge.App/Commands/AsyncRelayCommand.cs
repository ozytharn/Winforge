using System.Windows.Input;

namespace WinForge.App.Commands;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool> canExecute,
    Action<Exception> onError) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            onError(exception);
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
