using Avalonia.Controls;

namespace MegaDl.Desktop.Views;

// Code-behind stays (almost) empty in MVVM: behaviour lives in the ViewModel,
// so it can be tested without opening a window.
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
