using System.Diagnostics;
using System.Management.Automation;
using System.Windows;
using System.Windows.Threading;
using ExchangeCertManager.Helpers;
using ExchangeCertManager.Services;
using ExchangeCertManager.ViewModels;

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

        var shotIndex = Array.FindIndex(e.Args, a => a.Equals(DemoMode.ScreenshotArgument, StringComparison.OrdinalIgnoreCase));
        string? shotFolder = shotIndex >= 0 ? (shotIndex + 1 < e.Args.Length ? e.Args[shotIndex + 1] : "screenshots") : null;
        var demo = shotFolder is not null || e.Args.Any(a => a.Equals(DemoMode.DemoArgument, StringComparison.OrdinalIgnoreCase));

        _headless = shotFolder is not null;
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

            if (shotFolder is not null) await DemoMode.CaptureSplashAsync(splash, shotFolder);

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

        if (shotFolder is not null)
        {
            // Screenshots at a typical desktop size, whatever the build machine's screen.
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = 0; main.Top = 0; main.Width = 1440; main.Height = 900;
        }

        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        main.Activate();
        splash.Close();

        if (!demo && main.DataContext is MainViewModel live)
        {
            // Tidy up after an update, then (if enabled in Tools > Options) look for a newer version.
            // Both run in the background and never block or interrupt.
            _ = UpdateService.CleanUpAsync();
            if (live.State.Settings.CheckForUpdatesAtStartup) _ = live.CheckForUpdatesAsync(quiet: true);
        }

        if (demo && main.DataContext is MainViewModel vm)
        {
            DemoMode.Seed(vm);
            vm.Navigate("inventory");
            if (shotFolder is not null)
            {
                await DemoMode.CaptureAsync(main, vm, shotFolder);
                Shutdown(0);
            }
        }
    }

    private bool _headless;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = e.Exception;
        while (root.InnerException is not null) root = root.InnerException;
        Log.Error($"Unexpected error: {root.GetType().Name}: {root.Message} | {string.Join(" <- ", (root.StackTrace ?? "").Split('\n').Take(6).Select(l => l.Trim()))}");
        if (_headless) { e.Handled = true; return; } // screenshot run: never block on a dialog
        MessageBox.Show(e.Exception.Message, "Exchange Certificate Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
