using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MegaDl.Desktop.Services;
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
            // Phase 3 swaps SimulatedTransferEngine for the real TransferManager here.
            var engine = new SimulatedTransferEngine();
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(engine) };
            desktop.ShutdownRequested += (_, _) => engine.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
