using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WindowRecall.App;

/// <summary>Minimal observable base for the App view models; keeps Avalonia out of testable logic.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Synchronous relay command.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action execute;
    private readonly Func<bool>? canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() != false;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Asynchronous command that refuses to start a second run while one is active and reports
/// failures through <see cref="Failure"/> instead of throwing across the XAML boundary.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> executeAsync;
    private readonly Func<bool>? canExecute;
    private bool isRunning;

    public AsyncCommand(Func<object?, Task> executeAsync, Func<bool>? canExecute = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public Exception? Failure { get; private set; }

    public bool IsRunning => isRunning;

    public bool CanExecute(object? parameter) => !isRunning && canExecute?.Invoke() != false;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return;
        isRunning = true;
        Failure = null;
        RaiseCanExecuteChanged();
        try
        {
            await executeAsync(parameter).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    async void ICommand.Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(false);
}

/// <summary>Observable progress state driven by the view models.</summary>
public sealed class ProgressState : ViewModelBase
{
    private bool isBusy;
    private string label = string.Empty;
    private double? maximum;
    private double value;

    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (SetProperty(ref isBusy, value))
                OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool IsVisible => isBusy;

    public string Label
    {
        get => label;
        set => SetProperty(ref label, value);
    }

    public double? Maximum
    {
        get => maximum;
        set => SetProperty(ref maximum, value);
    }

    public double Value
    {
        get => value;
        set => SetProperty(ref this.value, value);
    }
}
