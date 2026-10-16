using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MegaDl.Core.Settings;
using MegaDl.Core.Transfers;
using MegaDl.Desktop.ViewModels;
using MegaDl.Desktop.Views;

namespace MegaDl.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _isExiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            // Composition root: the one place that decides which engine the app uses.
            var settings = AppSettings.Load();
            SettingsViewModel.ApplyTheme(settings.Theme);
            SettingsViewModel.ApplyFont(settings.FontFamily, settings.FontSize);

            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaDl/0.1");
            var engine = new TransferManager(http, settings: settings);
            engine.Restore(); // Phase 5: pick up wherever last session's queue left off

            var mainWindow = new MainWindow { DataContext = new MainWindowViewModel(engine, settings) };
            mainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) => { engine.Dispose(); http.Dispose(); };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Phase 5: closing the window just hides it — downloads keep running in the
    // background. The tray icon's "Quit" (below) is the only real way out.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        (sender as Window)?.Hide();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();
    private void OnTrayOpenClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        if (_desktop?.MainWindow is not { } window) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnTrayQuitClicked(object? sender, EventArgs e)
    {
        _isExiting = true;
        _desktop?.Shutdown();
    }
}
