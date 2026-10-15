using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MegaDl.Desktop.ViewModels;

namespace MegaDl.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsViewModel vm) : this()
    {
        DataContext = vm;
        vm.BrowseDownloadFolderRequested += OnBrowseDownloadFolderRequested;
    }

    private async void OnBrowseDownloadFolderRequested(Action<string?> setResult)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a download folder",
            AllowMultiple = false,
        });
        setResult(folders.Count > 0 ? folders[0].TryGetLocalPath() : null);
    }
}
