using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Orion.App.Mvvm;

/// <summary>Minimal MVVM base class (no third-party dependencies in the shipped EXE).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for a computed property.</summary>
    public void RaisePropertyChanged(string propertyName) => OnPropertyChanged(propertyName);
}

/// <summary>Synchronous command.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command that prevents re-entrancy while running and surfaces exceptions instead of
/// crashing the dispatcher thread.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), onError)
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_onError is not null) _onError(ex);
            else MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Observable collection with helpers to replace or append many items at once.</summary>
public sealed class ObservableList<T> : ObservableCollection<T>
{
    public void Replace(IEnumerable<T> items)
    {
        Clear();
        foreach (var item in items) Add(item);
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items) Add(item);
    }
}
