using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaDl.Core;
using MegaDl.Core.Api;
using MegaDl.Core.Links;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// Backs the folder-picker dialog (Phase 2b): fetches the folder's listing once,
/// builds a tree the view binds a <c>TreeView</c> to, and on "Download selected"
/// hands the chosen files back to whoever opened it (<see cref="MainWindowViewModel"/>,
/// via <see cref="MegaDl.Desktop.Views.FolderPickerWindow"/>'s dialog result).
///
/// Java port note: FolderLinkDialog, behaviour only — the tri-state checkbox cascade
/// lives in <see cref="FolderPickerNodeViewModel"/>.
/// </summary>
public partial class FolderPickerViewModel(MegaApiClient api, MegaLink link) : ObservableObject
{
    public ObservableCollection<FolderPickerNodeViewModel> Roots { get; } = [];

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _selectionText = "Nothing selected";

    private IReadOnlyDictionary<string, FolderNode> _byHandle = new Dictionary<string, FolderNode>();

    /// <summary>Set by the view when "Download selected" is pressed; null means cancelled.</summary>
    public IReadOnlyList<FolderNode>? Result { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<FolderNode> nodes = await api.GetFolderAsync(link, ct);
            _byHandle = nodes.ToDictionary(n => n.Handle);

            // Nodes with no parent in this listing (or whose parent is the share root,
            // which MEGA doesn't send as a node) are the tree's top level.
            var byParent = nodes.ToLookup(n => n.ParentHandle is not null && _byHandle.ContainsKey(n.ParentHandle) ? n.ParentHandle : null);
            foreach (FolderNode node in byParent[null].OrderBy(n => !n.IsFolder).ThenBy(n => n.Name))
                Roots.Add(BuildTree(node, byParent, parent: null, rootOnChanged: UpdateSelectionText));
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static FolderPickerNodeViewModel BuildTree(
        FolderNode node, ILookup<string?, FolderNode> byParent, FolderPickerNodeViewModel? parent, Action? rootOnChanged = null)
    {
        var vm = new FolderPickerNodeViewModel(node, node.Name, node.IsFolder, node.Size, parent, parent is null ? rootOnChanged : null);
        if (node.IsFolder)
            foreach (FolderNode child in byParent[node.Handle].OrderBy(n => !n.IsFolder).ThenBy(n => n.Name))
                vm.Children.Add(BuildTree(child, byParent, vm));
        return vm;
    }

    [RelayCommand]
    private void DownloadSelected()
    {
        List<FolderNode> selected = Roots.SelectMany(r => r.SelectedFiles()).ToList();
        Result = selected;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke();
    }

    /// <summary>Set by the view; called once the user is done (either button).</summary>
    public Action? RequestClose { get; set; }

    public IReadOnlyDictionary<string, FolderNode> ByHandle => _byHandle;

    partial void OnIsLoadingChanged(bool value) => UpdateSelectionText();

    internal void UpdateSelectionText()
    {
        long count = Roots.Sum(r => r.SelectedFiles().Count());
        long size = Roots.Sum(r => r.SelectedSize());
        SelectionText = count == 0 ? "Nothing selected" : $"{count} file(s) selected · {ByteSize.Format(size)}";
    }
}
