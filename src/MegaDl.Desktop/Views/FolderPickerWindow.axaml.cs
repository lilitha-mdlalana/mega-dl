using Avalonia.Controls;
using MegaDl.Desktop.ViewModels;

namespace MegaDl.Desktop.Views;

public partial class FolderPickerWindow : Window
{
    public FolderPickerWindow() => InitializeComponent();

    public FolderPickerWindow(FolderPickerViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose = Close;
        _ = vm.LoadAsync();
    }
}
