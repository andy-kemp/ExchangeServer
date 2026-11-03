using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;

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
        _currentPage = Connect;
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsConnected)) OnPropertyChanged(nameof(IsConnected));
        };
    }

    public AppState State => AppState.Current;
    public ObservableCollection<LogEntry> LogEntries => Log.Entries;
    public bool IsConnected => State.IsConnected;

    public string VersionText => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    public ConnectViewModel Connect { get; }
    public InventoryViewModel Inventory { get; }
    public RequestViewModel Request { get; }
    public CompleteViewModel Complete { get; }
    public ImportViewModel Import { get; }
    public DeployViewModel Deploy { get; }
    public AcmeViewModel Acme { get; }
    public HealthViewModel Health { get; }

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

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var name in new[] { nameof(IsConnectPage), nameof(IsInventoryPage), nameof(IsRequestPage), nameof(IsCompletePage), nameof(IsImportPage),
                                     nameof(IsDeployPage), nameof(IsAcmePage), nameof(IsHealthPage) })
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
}
