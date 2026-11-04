using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;
using ExchangeCertManager.Views;

namespace ExchangeCertManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Connect = new ConnectViewModel(this);
        Inventory = new InventoryViewModel(this);
        Request = new RequestViewModel(this);
        Complete = new CompleteViewModel(this);
        Import = new ImportViewModel(this);
        Deploy = new DeployViewModel(this);
        Acme = new AcmeViewModel(this);
        Health = new HealthViewModel(this);
        Urls = new UrlsViewModel(this);
        _currentPage = Connect;
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsConnected)) OnPropertyChanged(nameof(IsConnected));
        };
    }

    public AppState State => AppState.Current;
    public ObservableCollection<LogEntry> LogEntries => Log.Entries;
    public bool IsConnected => State.IsConnected;

    public string VersionText => $"v{UpdateService.CurrentText}";

    /// <summary>Set when the startup check finds a newer version; shows the update button in the status bar.</summary>
    [ObservableProperty] private UpdateInfo? _availableUpdate;
    public string UpdateButtonText => AvailableUpdate is null ? "" : $"Update to {AvailableUpdate.Version}";
    partial void OnAvailableUpdateChanged(UpdateInfo? value) => OnPropertyChanged(nameof(UpdateButtonText));

    public ConnectViewModel Connect { get; }
    public InventoryViewModel Inventory { get; }
    public RequestViewModel Request { get; }
    public CompleteViewModel Complete { get; }
    public ImportViewModel Import { get; }
    public DeployViewModel Deploy { get; }
    public AcmeViewModel Acme { get; }
    public HealthViewModel Health { get; }
    public UrlsViewModel Urls { get; }

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private bool _showLog = true;

    // One bool per nav item so the radio buttons follow programmatic navigation.
    public bool IsConnectPage { get => CurrentPage == Connect; set { if (value) Navigate("connect"); } }
    public bool IsInventoryPage { get => CurrentPage == Inventory; set { if (value) Navigate("inventory"); } }
    public bool IsRequestPage { get => CurrentPage == Request; set { if (value) Navigate("request"); } }
    public bool IsCompletePage { get => CurrentPage == Complete; set { if (value) Navigate("complete"); } }
    public bool IsImportPage { get => CurrentPage == Import; set { if (value) Navigate("import"); } }
    public bool IsDeployPage { get => CurrentPage == Deploy; set { if (value) Navigate("deploy"); } }
    public bool IsAcmePage { get => CurrentPage == Acme; set { if (value) Navigate("acme"); } }
    public bool IsHealthPage { get => CurrentPage == Health; set { if (value) Navigate("health"); } }
    public bool IsUrlsPage { get => CurrentPage == Urls; set { if (value) Navigate("urls"); } }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var name in new[] { nameof(IsConnectPage), nameof(IsInventoryPage), nameof(IsRequestPage), nameof(IsCompletePage), nameof(IsImportPage),
                                     nameof(IsDeployPage), nameof(IsAcmePage), nameof(IsHealthPage), nameof(IsUrlsPage) })
            OnPropertyChanged(name);
        value.OnShown();
    }

    public void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "inventory" => Inventory,
            "request" => Request,
            "complete" => Complete,
            "import" => Import,
            "deploy" => Deploy,
            "acme" => Acme,
            "health" => Health,
            "urls" => Urls,
            _ => Connect,
        };
    }

    [RelayCommand]
    private void ClearLog() => Log.Entries.Clear();

    [RelayCommand]
    private void OpenLogFolder()
    {
        System.IO.Directory.CreateDirectory(Log.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", Log.LogDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ToggleLog() => ShowLog = !ShowLog;

    // ---- Menu -------------------------------------------------------------------------------------------------

    public bool CanInstall => !InstallService.IsRunningInstalledCopy;

    [RelayCommand]
    private void Install()
    {
        if (State.IsDemo) { AppState.Inform("Nothing is installed in demo mode."); return; }
        if (!AppState.Confirm($"Install Exchange Certificate Manager for everyone on this server?\n\nThe EXE is copied to {AppPaths.InstallDirectory} and a Start menu shortcut is added. Its data stays in {AppPaths.ProgramData}, and the renewal task runs the installed copy.")) return;
        try
        {
            InstallService.Install();
            if (AppState.Confirm("Installed. Open the installed copy now and close this one?"))
            {
                InstallService.StartInstalledCopy();
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Install failed: {ex.Message}");
            AppState.Inform($"Couldn't install: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Exit() => System.Windows.Application.Current.MainWindow?.Close();

    [RelayCommand]
    private void OpenOptions() => OptionsDialog.Show(State.Settings, () => _ = CheckForUpdatesAsync(quiet: false));

    [RelayCommand]
    private void OpenDocs() => UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/");

    [RelayCommand]
    private void OpenQuickStart() => UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/quick-start/");

    [RelayCommand]
    private void OpenTroubleshooting() => UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/troubleshooting/");

    [RelayCommand]
    private void OpenWhatsNew() => UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/whats-new/");

    [RelayCommand]
    private void OpenDownloads() => UpdateService.OpenUrl(UpdateService.DownloadsPage);

    [RelayCommand]
    private void OpenTour() => UpdateService.OpenUrl("https://demos.andykemp.com/exchange-cert-manager/live/");

    [RelayCommand]
    private void ReportIssue() => UpdateService.OpenUrl("https://www.andykemp.com/contact/");

    [RelayCommand]
    private void ShowAbout() => new AboutWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesAsync(quiet: false);

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is { } info) await OfferUpdateAsync(info);
    }

    /// <summary>Looks for a newer version. Quiet (the startup check) only logs and shows the status-bar button;
    /// otherwise the result is shown and an update is offered.</summary>
    public async Task CheckForUpdatesAsync(bool quiet)
    {
        if (State.IsDemo) { if (!quiet) AppState.Inform("Updates aren't checked in demo mode."); return; }
        try
        {
            var info = await UpdateService.CheckAsync();
            AvailableUpdate = info;
            if (info is null)
            {
                if (quiet) Log.Info($"Version {UpdateService.CurrentText} is the latest.");
                else AppState.Inform($"You have the latest version ({UpdateService.CurrentText}).");
                return;
            }
            Log.Info($"Version {info.Version} is available (you have {UpdateService.CurrentText}). Tools > Check for updates, or the button at the bottom right.");
            if (!quiet) await OfferUpdateAsync(info);
        }
        catch (Exception ex)
        {
            if (quiet) Log.Warn($"Couldn't check for updates: {ex.Message}");
            else AppState.Inform($"Couldn't check for updates: {ex.Message}\n\nThe latest version is always at downloads.andykemp.com.");
        }
    }

    private async Task OfferUpdateAsync(UpdateInfo info)
    {
        if (State.IsBusy) { AppState.Inform("Wait for the current task to finish, then update."); return; }
        if (!UpdateDialog.Ask(info)) return;
        if (UpdateService.ExePath is null)
        {
            UpdateService.OpenUrl(UpdateService.DownloadsPage);
            return;
        }
        // The update runs in the splash, as WinImagePrep's updater: "Updating", from and to versions, progress
        var from = UpdateService.CurrentText;
        var window = new SplashWindow { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowUpdate(from, info.Version);
        window.Show();
        string exe;
        State.IsBusy = true;
        State.BusyText = $"Updating to version {info.Version}…";
        try
        {
            exe = await UpdateService.DownloadAndInstallAsync(info, new Progress<(string Text, double? Percent)>(p =>
            {
                window.SetStatus(p.Text);
                window.SetProgress(p.Percent);
            }));
            window.SetStatus($"Updated to {info.Version}. Restarting…");
            window.SetProgress(100);
            await Task.Delay(1200);
        }
        catch (Exception ex)
        {
            window.Close();
            Log.Error($"Update failed: {ex.Message}");
            AppState.Inform($"The update didn't install: {ex.Message}\n\nNothing was changed. You can download it from downloads.andykemp.com.");
            return;
        }
        finally { State.IsBusy = false; }

        UpdateService.Restart(exe);
        System.Windows.Application.Current.Shutdown();
    }
}
