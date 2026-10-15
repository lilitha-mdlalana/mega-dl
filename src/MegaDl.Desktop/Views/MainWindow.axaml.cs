using Avalonia.Controls;
using MegaDl.Core.Api;
using MegaDl.Desktop.ViewModels;

namespace MegaDl.Desktop.Views;

// Code-behind stays (almost) empty in MVVM: behaviour lives in the ViewModel,
// so it can be tested without opening a window. The one exception is the folder
// picker — showing a dialog is inherently a View concern — handled below.
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.FolderPickerRequested += OnFolderPickerRequested;
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
}
