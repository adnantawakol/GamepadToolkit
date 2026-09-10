using System.Windows;

namespace GamepadToolkit.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The setup dialog is the first window shown, which would otherwise make it the main
        // window and shut the app down the moment it closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Runs before the main window, which probes both drivers as it builds and would
        // otherwise open showing warnings the user has no obvious way to act on. The portable
        // build especially: it may be the first time this machine has seen the toolkit.
        DriverSetupWindow.PromptIfNeeded();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
