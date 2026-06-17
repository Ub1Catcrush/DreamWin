using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DreamWin.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasError;

    protected async Task RunAsync(Func<Task> action, string? busyMessage = null)
    {
        Debug.WriteLine($"[{GetType().Name}] RunAsync start: {busyMessage}");
        IsBusy = true;
        HasError = false;
        StatusMessage = busyMessage ?? "";
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{GetType().Name}] RunAsync exception: {ex}");
            HasError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Debug.WriteLine($"[{GetType().Name}] RunAsync end, IsBusy=false");
        }
    }
}
