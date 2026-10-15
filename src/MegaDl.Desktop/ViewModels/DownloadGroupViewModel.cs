using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core;
using MegaDl.Core.Transfers;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// One collapsible section in the download list (Phase 2c): either every file queued
/// from one folder link, or the shared "Single files" bucket for plain file links.
/// <see cref="MainWindowViewModel.Sync"/> keeps <see cref="Rows"/> in sync with the
/// engine the same way it keeps the flat list in sync — one level higher, by group key.
/// </summary>
public partial class DownloadGroupViewModel : ObservableObject
{
    public const string SingleFilesKey = "";

    public string Key { get; }
    public bool IsSingleFiles => Key == SingleFilesKey;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private double _overallPercent;

    public ObservableCollection<TransferItemViewModel> Rows { get; } = [];

    private bool _wasAllDone;

    public DownloadGroupViewModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    /// <summary>Recomputes totals and visibility. Call after every <see cref="MainWindowViewModel.Sync"/> pass.</summary>
    public void Recompute()
    {
        int done = 0, failed = 0;
        long bytesDone = 0, bytesTotal = 0;
        double speed = 0;

        foreach (TransferItemViewModel row in Rows)
        {
            row.IsVisibleInList = !row.IsCompleted || ShowCompleted;
            if (row.IsCompleted) done++;
            if (row.IsFailed) failed++;
            bytesDone += row.BytesDone;
            bytesTotal += row.SizeBytes;
            speed += row.BytesPerSecond;
        }

        OverallPercent = bytesTotal == 0 ? 0 : bytesDone * 100.0 / bytesTotal;

        string speedText = speed > 0 ? $" · {ByteSize.Format(speed)}/s" : "";
        string failedText = failed > 0 ? $" · {failed} failed" : "";
        SummaryText = $"{done} / {Rows.Count} files · {ByteSize.Format(bytesDone)} / {ByteSize.Format(bytesTotal)}{speedText}{failedText}";

        bool allDone = Rows.Count > 0 && Rows.All(r => r.State is TransferState.Completed or TransferState.Failed);
        if (allDone && !_wasAllDone) IsExpanded = false; // collapses itself once, doesn't fight a manual re-expand
        _wasAllDone = allDone;
    }

    partial void OnShowCompletedChanged(bool value) => Recompute();

    [RelayCommand] private void PauseGroup() { foreach (var r in Rows) r.PauseCommand.Execute(null); }
    [RelayCommand] private void ResumeGroup() { foreach (var r in Rows) r.ResumeCommand.Execute(null); }
    [RelayCommand] private void CancelGroup() { foreach (var r in Rows) r.CancelCommand.Execute(null); }
}
