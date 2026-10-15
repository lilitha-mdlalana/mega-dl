using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaDl.Core;
using MegaDl.Core.Api;

namespace MegaDl.Desktop.ViewModels;

/// <summary>
/// One row in the folder picker's tree. A folder's checkbox is tri-state: checking or
/// unchecking it cascades to every descendant, and checking/unchecking a leaf bubbles
/// back up to recompute its ancestors' (possibly "mixed", i.e. null) state.
/// </summary>
public partial class FolderPickerNodeViewModel : ObservableObject
{
    public FolderNode? Node { get; } // null for the synthetic tree root
    public string Name { get; }
    public bool IsFolder { get; }
    public string SizeText { get; }
    public FolderPickerNodeViewModel? Parent { get; }
    public ObservableCollection<FolderPickerNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool? _isChecked = false;
    private bool _suppressCascade;
    private readonly Action? _onChanged;

    public FolderPickerNodeViewModel(FolderNode? node, string name, bool isFolder, long size,
        FolderPickerNodeViewModel? parent, Action? onChanged = null)
    {
        Node = node;
        Name = name;
        IsFolder = isFolder;
        SizeText = isFolder ? "" : ByteSize.Format(size);
        Parent = parent;
        _onChanged = onChanged ?? (parent is null ? null : parent.NotifyChanged);
    }

    private void NotifyChanged() => _onChanged?.Invoke();

    /// <summary>Every selected (checked) file leaf under this node, including itself.</summary>
    public IEnumerable<FolderNode> SelectedFiles() =>
        (!IsFolder && IsChecked == true && Node is not null ? [Node] : Enumerable.Empty<FolderNode>())
        .Concat(Children.SelectMany(c => c.SelectedFiles()));

    public long SelectedSize() =>
        (!IsFolder && IsChecked == true && Node is not null ? Node.Size : 0) + Children.Sum(c => c.SelectedSize());

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppressCascade) return;

        if (value != null)
        {
            _suppressCascade = true;
            foreach (var child in Children) child.IsChecked = value;
            _suppressCascade = false;
        }

        Parent?.RecomputeFromChildren();
        NotifyChanged();
    }

    private void RecomputeFromChildren()
    {
        if (Children.Count == 0) return;

        bool allTrue = Children.All(c => c.IsChecked == true);
        bool allFalse = Children.All(c => c.IsChecked == false);

        _suppressCascade = true;
        IsChecked = allTrue ? true : allFalse ? false : null;
        _suppressCascade = false;

        Parent?.RecomputeFromChildren();
    }
}
