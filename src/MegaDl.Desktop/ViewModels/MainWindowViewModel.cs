using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core;
using MegaDl.Core.Api;
using MegaDl.Core.Links;
using MegaDl.Core.Settings;
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
    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, TransferItemViewModel> _byId = [];
    private readonly Dictionary<Guid, string> _groupKeyById = [];
    private readonly Dictionary<string, DownloadGroupViewModel> _groupsByKey = [];

    // What's already been queued (by this session), so the clipboard watcher doesn't
    // re-prompt for a link the user already asked for.
    private readonly HashSet<string> _queuedLinkKeys = [];

    private string DownloadDir => _settings.ResolvedDownloadFolder;

    /// <summary>One collapsible section per folder link, plus a shared "Single files" one (Phase 2c).</summary>
    public ObservableCollection<DownloadGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddLinksCommand))]
    private string _linkText = "";

    [ObservableProperty] private string? _linkError;
    [ObservableProperty] private string _statusText = "Paste a MEGA link to start.";
    [ObservableProperty] private string _totalSpeedText = "";
    [ObservableProperty] private bool _isEmpty = true;

    // "Found N MEGA links. Download? [Download] [Ignore]" — Phase 3's clipboard watcher
    // asks, it never auto-starts.
    [ObservableProperty] private bool _clipboardPromptVisible;
    [ObservableProperty] private string _clipboardPromptText = "";
    private IReadOnlyList<MegaLink> _clipboardPromptLinks = [];

    /// <summary>Raised when a pasted link is a folder — the view shows the picker dialog
    /// and reports back which files (if any) got chosen.</summary>
    public event Action<FolderPickerRequest>? FolderPickerRequested;

    /// <summary>Raised by the "Settings" button — the view owns showing that dialog.</summary>
    public event Action? SettingsRequested;

    public AppSettings Settings => _settings;

    /// <summary>Exposed so the Settings dialog can reload the proxy list and the status bar can read it (Phase 4).</summary>
    public ITransferEngine Engine => _engine;

    [ObservableProperty] private string _smartProxyStatusText = "";

    /// <summary>Used by the XAML previewer only.</summary>
    public MainWindowViewModel() : this(new SimulatedTransferEngine(), new AppSettings()) { }

    public MainWindowViewModel(ITransferEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
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

        QueueLinks(links);
        LinkError = null;
        LinkText = "";
    }

    [RelayCommand]
    private void AcceptClipboardPrompt()
    {
        ClipboardPromptVisible = false;
        QueueLinks(_clipboardPromptLinks);
    }

    [RelayCommand]
    private void DismissClipboardPrompt() => ClipboardPromptVisible = false;

    /// <summary>Called by the clipboard watcher (a View-layer service — it needs a TopLevel)
    /// whenever it spots new MEGA links. Never auto-starts: shows the ask-first banner instead.</summary>
    public void OnClipboardLinksFound(IReadOnlyList<MegaLink> links)
    {
        List<MegaLink> newLinks = links.Where(l => !_queuedLinkKeys.Contains(LinkKey(l))).ToList();
        if (newLinks.Count == 0) return;

        _clipboardPromptLinks = newLinks;
        ClipboardPromptText = newLinks.Count == 1
            ? "Found a MEGA link on your clipboard."
            : $"Found {newLinks.Count} MEGA links on your clipboard.";
        ClipboardPromptVisible = true;
    }

    private void QueueLinks(IEnumerable<MegaLink> links)
    {
        foreach (var link in links)
        {
            _queuedLinkKeys.Add(LinkKey(link));
            if (link.Kind == MegaLinkKind.Folder)
                FolderPickerRequested?.Invoke(new FolderPickerRequest(link, OnFolderPicked(link)));
            else
                _engine.Enqueue(link, DownloadDir);
        }
        Sync(); // show the new rows immediately instead of waiting for the next tick
    }

    private static string LinkKey(MegaLink link) => $"{link.Kind}:{link.Id}:{link.ChildId}";

    private Action<IReadOnlyList<FolderNode>?> OnFolderPicked(MegaLink folderLink) => chosen =>
    {
        if (chosen is null) return; // cancelled

        foreach (FolderNode node in chosen)
            _engine.Enqueue(new MegaLink(MegaLinkKind.FileInFolder, folderLink.Id, folderLink.Key, node.Handle), DownloadDir);
        Sync();
    };

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void PauseAll() { foreach (var d in _byId.Values) _engine.Pause(d.Id); }

    [RelayCommand]
    private void ResumeAll() { foreach (var d in _byId.Values) _engine.Resume(d.Id); }

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var d in _byId.Values.Where(d => d.State == TransferState.Completed).ToList())
            _engine.Cancel(d.Id);
        Sync();
    }

    /// <summary>Reconciles the group list and each group's rows with the engine's latest
    /// snapshot (like a React diff, by key) — the same pattern as before 2c, one level higher.</summary>
    private void Sync()
    {
        IReadOnlyList<TransferSnapshot> snapshot = _engine.Snapshot();
        var aliveItems = new HashSet<Guid>();
        var aliveGroups = new HashSet<string>();

        foreach (var s in snapshot)
        {
            string groupKey = s.GroupId ?? DownloadGroupViewModel.SingleFilesKey;
            aliveGroups.Add(groupKey);
            if (!_groupsByKey.TryGetValue(groupKey, out var group))
            {
                string groupName = groupKey == DownloadGroupViewModel.SingleFilesKey ? "Single files" : s.GroupName ?? "Folder";
                group = new DownloadGroupViewModel(groupKey, groupName);
                _groupsByKey[groupKey] = group;
                Groups.Add(group);
            }
            else if (!group.IsSingleFiles && s.GroupName is { } realName && group.Name != realName)
            {
                group.Name = realName; // placeholder "Folder" -> the real folder name, once resolved
            }

            aliveItems.Add(s.Id);
            if (!_byId.TryGetValue(s.Id, out var vm))
            {
                vm = new TransferItemViewModel(s.Id, _engine);
                _byId[s.Id] = vm;
                _groupKeyById[s.Id] = groupKey;
                group.Rows.Add(vm);
            }
            else if (_groupKeyById[s.Id] != groupKey)
            {
                // A file-in-folder job starts out with no GroupId (shown under "Single
                // files") until its folder listing resolves — move it once that lands,
                // instead of leaving a now-empty group behind.
                if (_groupsByKey.TryGetValue(_groupKeyById[s.Id], out var oldGroup)) oldGroup.Rows.Remove(vm);
                group.Rows.Add(vm);
                _groupKeyById[s.Id] = groupKey;
            }
            vm.Apply(s);
        }

        foreach (var gone in _byId.Keys.Where(id => !aliveItems.Contains(id)).ToList())
        {
            if (_groupsByKey.TryGetValue(_groupKeyById[gone], out var group)) group.Rows.Remove(_byId[gone]);
            _byId.Remove(gone);
            _groupKeyById.Remove(gone);
        }

        foreach (var goneGroup in _groupsByKey.Keys.Where(k => !aliveGroups.Contains(k)).ToList())
        {
            Groups.Remove(_groupsByKey[goneGroup]);
            _groupsByKey.Remove(goneGroup);
        }

        foreach (var group in Groups) group.Recompute();

        int running = snapshot.Count(s => s.State == TransferState.Running);
        int queued = snapshot.Count(s => s.State is TransferState.Queued or TransferState.Starting);
        int done = snapshot.Count(s => s.State == TransferState.Completed);
        double speed = snapshot.Sum(s => s.BytesPerSecond);

        IsEmpty = Groups.Count == 0;
        TotalSpeedText = speed > 0 ? $"↓ {ByteSize.Format(speed)}/s" : "";
        StatusText = IsEmpty
            ? "Paste a MEGA link to start."
            : $"{running} downloading · {queued} queued · {done} done";

        SmartProxyStatusText = _settings.SmartProxyEnabled && _engine is TransferManager { ProxyPool: { } pool }
            ? $"SmartProxy: ON ({pool.AvailableCount} available)"
            : "";
    }
}
