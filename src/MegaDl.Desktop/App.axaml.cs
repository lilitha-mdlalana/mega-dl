using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MegaDl.Core.Settings;
using MegaDl.Core.Transfers;
using MegaDl.Desktop.ViewModels;
using MegaDl.Desktop.Views;

namespace MegaDl.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: the one place that decides which engine the app uses.
            var settings = AppSettings.Load();
            SettingsViewModel.ApplyTheme(settings.Theme);
            SettingsViewModel.ApplyFont(settings.FontFamily, settings.FontSize);

            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MegaDl/0.1");
            var engine = new TransferManager(http);
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(engine, settings) };
            desktop.ShutdownRequested += (_, _) => { engine.Dispose(); http.Dispose(); };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
