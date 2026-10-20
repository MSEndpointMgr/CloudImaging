using System.ComponentModel;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>A hosted view model whose long-running operation must be cancelled before the window closes.</summary>
public interface IOperationHost : INotifyPropertyChanged
{
    /// <summary>True while an operation is running.</summary>
    bool IsOperationRunning { get; }

    /// <summary>Name of the running operation, e.g. "Boot image generation".</summary>
    string RunningOperationName { get; }

    /// <summary>What cancelling the running operation leaves behind.</summary>
    string RunningOperationCloseWarning { get; }

    /// <summary>Requests cancellation; the operation runs its own cleanup before it stops.</summary>
    void CancelRunningOperation();
}
