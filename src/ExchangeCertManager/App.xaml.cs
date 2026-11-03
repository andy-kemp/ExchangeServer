using System.Windows;
using System.Windows.Threading;
using ExchangeCertManager.Services;

namespace ExchangeCertManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless mode for the scheduled Let's Encrypt renewal.
        if (e.Args.Any(a => a.Equals(RenewalRunner.Argument, StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var force = e.Args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
            var code = 1;
            try { code = await RenewalRunner.RunAsync(force); }
            catch (Exception ex) { Log.Error($"Renewal run failed: {ex}"); }
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"Exchange Certificate Manager started on {Environment.MachineName}");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unexpected error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "Exchange Certificate Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
