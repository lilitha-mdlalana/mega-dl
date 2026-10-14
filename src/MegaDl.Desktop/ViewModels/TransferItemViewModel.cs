using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core;
using MegaDl.Core.Transfers;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// One row in the downloads list. Think of it like a React component's state:
/// the engine produces snapshots, <see cref="Apply"/> copies them in, and every
/// [ObservableProperty] that changes tells its bound control to redraw.
/// Java port note: replaces DownloadView.java (a Swing JPanel that set its own labels).
/// </summary>
public partial class TransferItemViewModel(Guid id, ITransferEngine engine) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private TransferState _state;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isCompleted;

    internal double BytesPerSecond { get; private set; }

    internal void Apply(TransferSnapshot s)
    {
        // Setting a property to the same value is a no-op (no redraw), so this is cheap.
        Name = s.Name;
        Percent = s.Size == 0 ? 0 : s.BytesDone * 100.0 / s.Size;
        SizeText = $"{ByteSize.Format(s.BytesDone)} of {ByteSize.Format(s.Size)}";
        SpeedText = s.State == TransferState.Running ? $"{ByteSize.Format(s.BytesPerSecond)}/s" : "";
        State = s.State;
        BytesPerSecond = s.BytesPerSecond;
        CanPause = s.State is TransferState.Queued or TransferState.Starting or TransferState.Running;
        CanResume = s.State is TransferState.Paused or TransferState.Failed;
        IsFailed = s.State == TransferState.Failed;
        IsCompleted = s.State == TransferState.Completed;
        StatusText = s.State switch
        {
            TransferState.Queued => "Queued",
            TransferState.Starting => "Fetching file info…",
            TransferState.Running when s.BytesPerSecond > 0 =>
                $"{Percent:0.0}% · {FormatEta((s.Size - s.BytesDone) / s.BytesPerSecond)} left",
            TransferState.Running => $"{Percent:0.0}%",
            TransferState.Paused => "Paused",
            TransferState.Verifying => "Verifying integrity…",
            TransferState.Completed => "Done · integrity OK",
            TransferState.Failed => s.Error ?? "Failed",
            _ => "",
        };
    }

    [RelayCommand] private void Pause() => engine.Pause(Id);
    [RelayCommand] private void Resume() => engine.Resume(Id);
    [RelayCommand] private void Cancel() => engine.Cancel(Id);

    private static string FormatEta(double seconds) =>
        seconds switch
        {
            < 60 => $"{seconds:0}s",
            < 3600 => $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s",
            _ => $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m",
        };
}
