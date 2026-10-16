using Avalonia.Controls;
using MegaDl.Core.Api;
using MegaDl.Core.Transfers;
using MegaDl.Desktop.Services;
using MegaDl.Desktop.ViewModels;

namespace MegaDl.Desktop.Views;

// Code-behind stays (almost) empty in MVVM: behaviour lives in the ViewModel,
// so it can be tested without opening a window. The exceptions are showing dialogs
// and the clipboard watcher — both need a TopLevel, which is inherently a View concern.
public partial class MainWindow : Window
{
    private ClipboardWatcher? _clipboardWatcher;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;

            vm.FolderPickerRequested += OnFolderPickerRequested;
            vm.SettingsRequested += OnSettingsRequested;

            _clipboardWatcher = new ClipboardWatcher(this) { Enabled = vm.Settings.ClipboardWatcherEnabled };
            _clipboardWatcher.LinksFound += vm.OnClipboardLinksFound;
            _clipboardWatcher.Start();
        };
    }

    private async void OnFolderPickerRequested(FolderPickerRequest request)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaDl/0.1");
        var pickerVm = new FolderPickerViewModel(new MegaApiClient(http), request.Link);
        var window = new FolderPickerWindow(pickerVm);
        await window.ShowDialog(this);
        request.Completed(pickerVm.Result);
    }

    private async void OnSettingsRequested()
    {
        var vm = (MainWindowViewModel)DataContext!;
        var settingsVm = new SettingsViewModel(vm.Settings, _clipboardWatcher, vm.Engine as TransferManager);
        var window = new SettingsWindow(settingsVm);
        await window.ShowDialog(this);
    }
}
