using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core;
using MegaDl.Core.Api;
using MegaDl.Core.Links;
using MegaDl.Core.Transfers;
using MegaDl.Desktop.Services;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// A pasted folder link needs a file picked before it can be downloaded. The view owns
/// showing the dialog (it's UI); this just hands over the link and gets back which files
/// (if any — null means cancelled) the user chose.
/// </summary>
public sealed record FolderPickerRequest(MegaLink Link, Action<IReadOnlyList<FolderNode>?> Completed);

/// <summary>
/// Java port note: replaces the transfer-list parts of MainPanel.java / MainPanelView.java.
///
/// The key production pattern lives in <see cref="Sync"/>: the engine can update
/// thousands of times a second across many chunk workers, but the UI samples it only
/// 4× a second on the UI thread. Smooth UI, no cross-thread bugs, no locks in views.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ITransferEngine _engine;
    private readonly Dictionary<Guid, TransferItemViewModel> _byId = [];
    private readonly string _downloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public ObservableCollection<TransferItemViewModel> Downloads { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddLinksCommand))]
    private string _linkText = "";

    [ObservableProperty] private string? _linkError;
    [ObservableProperty] private string _statusText = "Paste a MEGA link to start.";
    [ObservableProperty] private string _totalSpeedText = "";
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Raised when a pasted link is a folder — the view shows the picker dialog
    /// and reports back which files (if any) got chosen.</summary>
    public event Action<FolderPickerRequest>? FolderPickerRequested;

    /// <summary>Used by the XAML previewer only.</summary>
    public MainWindowViewModel() : this(new SimulatedTransferEngine()) { }

    public MainWindowViewModel(ITransferEngine engine)
    {
        _engine = engine;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Sync());
        timer.Start();
    }

    private bool CanAddLinks() => !string.IsNullOrWhiteSpace(LinkText);

    [RelayCommand(CanExecute = nameof(CanAddLinks))]
    private void AddLinks()
    {
        IReadOnlyList<MegaLink> links = MegaLink.FindAll(LinkText);
        if (links.Count == 0)
        {
            LinkError = "No MEGA links found. Expected something like https://mega.nz/file/…#…";
            return;
        }

        foreach (var link in links)
        {
            if (link.Kind == MegaLinkKind.Folder)
                FolderPickerRequested?.Invoke(new FolderPickerRequest(link, OnFolderPicked(link)));
            else
                _engine.Enqueue(link, _downloadDir);
        }

        LinkError = null;
        LinkText = "";
        Sync(); // show the new rows immediately instead of waiting for the next tick
    }

    private Action<IReadOnlyList<FolderNode>?> OnFolderPicked(MegaLink folderLink) => chosen =>
    {
        if (chosen is null) return; // cancelled

        foreach (FolderNode node in chosen)
            _engine.Enqueue(new MegaLink(MegaLinkKind.FileInFolder, folderLink.Id, folderLink.Key, node.Handle), _downloadDir);
        Sync();
    };

    [RelayCommand]
    private void PauseAll() { foreach (var d in Downloads) _engine.Pause(d.Id); }

    [RelayCommand]
    private void ResumeAll() { foreach (var d in Downloads) _engine.Resume(d.Id); }

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var d in Downloads.Where(d => d.State == TransferState.Completed).ToList())
            _engine.Cancel(d.Id);
        Sync();
    }

    /// <summary>Reconciles the list with the engine's latest snapshot (like a React diff, by key).</summary>
    private void Sync()
    {
        IReadOnlyList<TransferSnapshot> snapshot = _engine.Snapshot();
        var alive = new HashSet<Guid>();

        foreach (var s in snapshot)
        {
            alive.Add(s.Id);
            if (!_byId.TryGetValue(s.Id, out var vm))
            {
                vm = new TransferItemViewModel(s.Id, _engine);
                _byId[s.Id] = vm;
                Downloads.Add(vm);
            }
            vm.Apply(s);
        }

        foreach (var gone in _byId.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            Downloads.Remove(_byId[gone]);
            _byId.Remove(gone);
        }

        int running = snapshot.Count(s => s.State == TransferState.Running);
        int queued = snapshot.Count(s => s.State is TransferState.Queued or TransferState.Starting);
        int done = snapshot.Count(s => s.State == TransferState.Completed);
        double speed = snapshot.Sum(s => s.BytesPerSecond);

        IsEmpty = Downloads.Count == 0;
        TotalSpeedText = speed > 0 ? $"↓ {ByteSize.Format(speed)}/s" : "";
        StatusText = IsEmpty
            ? "Paste a MEGA link to start."
            : $"{running} downloading · {queued} queued · {done} done";
    }
}
