using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace LedvanceControl.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<object?, bool>? canExecute = null) : this(_ => execute(), canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Async command that disables itself while running and reports errors to the status bar.</summary>
public sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<object?, bool>? canExecute = null) : this(_ => execute(), canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (Exception ex)
        {
            Status.Error(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Global status line shown at the bottom of the main window.</summary>
public static class Status
{
    public static event Action<string, bool>? Changed;

    public static void Info(string text) => Post(text, false);
    public static void Error(string text) => Post(text, true);
    public static void Error(Exception ex) => Post(ex.Message, true);

    public static void Errors(string action, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) Info(action + " – done");
        else Error(action + " – " + string.Join("; ", errors));
    }

    private static void Post(string text, bool error)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() => Changed?.Invoke($"{DateTime.Now:HH:mm:ss}  {text}", error));
    }
}

/// <summary>Coalesces rapid changes (slider drags) into one action after a short pause.</summary>
public sealed class Debouncer(TimeSpan delay)
{
    private CancellationTokenSource? _cts;

    public void Run(Func<Task> action)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try { await action(); }
            catch (Exception ex) { Status.Error(ex); }
        }, TaskScheduler.Default);
    }
}

