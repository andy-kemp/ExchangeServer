using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

/// <summary>Shared connection, server list and inventory used by every page.</summary>
public sealed partial class AppState : ObservableObject
{
    public static AppState Current { get; } = new();

    [ObservableProperty] private ExchangeSession? _session;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public CertificateService? Certs { get; private set; }
    public HealthService? Health { get; private set; }
    public VirtualDirectoryService? Vdirs { get; private set; }
    public FixService? Fixes { get; private set; }

    public bool IsDemo => DemoConnection is not null && Session is null;

    /// <summary>False (with a message) in demo mode, where there's no Exchange to change.</summary>
    public bool RequireLive()
    {
        if (!IsDemo) return true;
        Inform("This is demo mode with made-up data, so nothing can be changed. Connect to Exchange to use it.");
        return false;
    }

    public ObservableCollection<ExchangeServerInfo> Servers { get; } = new();
    public ObservableCollection<CertificateGroup> Inventory { get; } = new();

    public bool IsConnected => Session is not null || DemoConnection is not null;
    public string ConnectionText => Session is not null ? $"{Session.ConnectedServer} · {Session.ConnectedAs}"
        : DemoConnection ?? "Not connected";

    /// <summary>Demo mode (--demo / --screenshots): sample data, no Exchange connection.</summary>
    public string? DemoConnection { get; private set; }

    public void EnterDemo(string connectionText)
    {
        DemoConnection = connectionText;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionText));
    }

    public UserSettings Settings { get; } = UserSettings.Load();

    /// <summary>Raised after the inventory is refreshed so pages can update their pickers.</summary>
    public event EventHandler? InventoryChanged;

    partial void OnSessionChanged(ExchangeSession? value)
    {
        Certs = value is null ? null : new CertificateService(value);
        Health = value is null || Certs is null ? null : new HealthService(value, Certs);
        Vdirs = value is null ? null : new VirtualDirectoryService(value);
        Fixes = value is null || Certs is null ? null : new FixService(value, Certs);
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionText));
    }

    public async Task RefreshAsync()
    {
        if (Certs is null) return;
        var servers = await Certs.GetServersAsync();
        Servers.Clear();
        foreach (var s in servers) Servers.Add(s);

        var (groups, _) = await Certs.GetInventoryAsync(servers);
        Inventory.Clear();
        foreach (var g in groups) Inventory.Add(g);
        Log.Info($"{groups.Count} certificates on {servers.Count} server(s)");
        InventoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs work with the busy overlay, logging and showing any error.</summary>
    public async Task<bool> RunAsync(string busyText, Func<Task> work)
    {
        if (IsBusy || !RequireLive()) return false;
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "Exchange Certificate Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public void SetBusyText(string text) => BusyText = text;

    public static bool Confirm(string message, string title = "Please confirm") =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Inform(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, "Exchange Certificate Manager", MessageBoxButton.OK, MessageBoxImage.Information);
}

public sealed class UserSettings
{
    public string LastServer { get; set; } = "";
    public string AcmeEmail { get; set; } = "";
    public string Organisation { get; set; } = "";
    public string City { get; set; } = "";
    public string County { get; set; } = "";
    public string Country { get; set; } = "GB";
    /// <summary>Look for a newer version on downloads.andykemp.com when the app opens (Tools > Options).</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.UserSettings))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(AppPaths.UserSettings)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UserSettings)!);
            File.WriteAllText(AppPaths.UserSettings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't save settings: {ex.Message}");
        }
    }
}
