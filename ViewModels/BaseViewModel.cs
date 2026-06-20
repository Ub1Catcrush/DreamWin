using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DreamWin.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasError;

    // Exposed for [RelayCommand(CanExecute = nameof(IsNotBusy))]
    public bool IsNotBusy => !IsBusy;

    // Cancellation support per ViewModel
    private CancellationTokenSource _loadCts = new();

    protected CancellationToken NewLoadToken()
    {
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    // Override in subclasses to enable the Retry button on error banners
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public virtual Task RetryAsync() => Task.CompletedTask;

    /// <summary>Marshals an action onto the UI dispatcher — safe to call from any thread.</summary>
    protected static Task OnUiAsync(Action action) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;

    protected async Task RunAsync(Func<Task> action, string? busyMessage = null)
    {
        Debug.WriteLine($"[{GetType().Name}] RunAsync start: {busyMessage}");
        IsBusy = true;
        HasError = false;
        StatusMessage = busyMessage ?? "";
        try
        {
            await action();
            StatusMessage = "";
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException or null)
        {
            Debug.WriteLine($"[{GetType().Name}] Timeout: {ex}");
            HasError = true;
            StatusMessage = "Request timed out. Check receiver connection.";
        }
        catch (OperationCanceledException)
        {
            // Not an error — load was superseded by a newer request
            Debug.WriteLine($"[{GetType().Name}] RunAsync cancelled");
            StatusMessage = "";
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[{GetType().Name}] Network error: {ex}");
            HasError = true;
            StatusMessage = $"Network error: {ex.Message}. Is the receiver reachable?";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{GetType().Name}] RunAsync exception: {ex}");
            HasError = true;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Debug.WriteLine($"[{GetType().Name}] RunAsync end");
        }
    }
}
