using System.Windows.Input;

namespace VocalTrainer.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);
    public void Execute(object? parameter) { if (parameter is T value && CanExecute(parameter)) execute(value); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
