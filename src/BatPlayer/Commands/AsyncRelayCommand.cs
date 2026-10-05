using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace BatPlayer.Commands;

/// <summary>
/// Асинхронная команда с поддержкой Task. Используется для длительных операций
/// (сканирование библиотеки, импорт плейлистов), чтобы UI не блокировался.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : new Func<object?, bool>(_ => canExecute()))
    {
    }

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        IsRunning = true;
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Services.Logger.Error(ex, "Async command failed");
        }
        finally
        {
            IsRunning = false;
        }
    }

    event EventHandler? ICommand.CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
