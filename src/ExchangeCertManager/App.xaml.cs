using System.Diagnostics;
using System.Management.Automation;
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

        // Splash first, as AKC-Audit and WinImagePrep. Explicit shutdown until the main window is up,
        // otherwise closing the splash would end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow();
        splash.Show();
        var shown = Stopwatch.StartNew();

        try
        {
            splash.SetStatus("Loading the PowerShell engine…");
            // Warm the hosted engine now so the first Connect doesn't stall.
            await Task.Run(() => { using var ps = PowerShell.Create(); });

            splash.SetStatus("Looking for Exchange on this machine…");
            var local = await Task.Run(ExchangeSession.DetectLocalExchangeServer);
            splash.SetStatus(local is null ? "Opening…" : $"Found {local}. Opening…");

            // Stay up for about two seconds, however quickly we're ready.
            var left = TimeSpan.FromSeconds(2) - shown.Elapsed;
            if (left > TimeSpan.Zero) await Task.Delay(left);
        }
        catch (Exception ex)
        {
            // Not fatal: log it and carry on to the main window.
            Log.Warn($"Startup check: {ex.Message}");
        }

        MainWindow main;
        try { main = new MainWindow(); }
        catch (Exception ex)
        {
            // Never leave the user with a splash that just disappears.
            splash.Close();
            MessageBox.Show($"Exchange Certificate Manager couldn't open: {ex.Message}", "Exchange Certificate Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        main.Activate();
        splash.Close();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unexpected error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "Exchange Certificate Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
