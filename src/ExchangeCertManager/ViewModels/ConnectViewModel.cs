using System.Management.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

public sealed partial class ConnectViewModel : PageViewModel
{
    public ConnectViewModel(MainViewModel main) : base(main)
    {
        LocalServer = ExchangeSession.DetectLocalExchangeServer();
        _server = LocalServer ?? State.Settings.LastServer;
        _userName = $"{Environment.UserDomainName}\\{Environment.UserName}";
        _ = DiscoverAsync();
    }

    /// <summary>Exchange servers found in AD, for the Server box's list.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DiscoveredServer> Discovered { get; } = new();
    [ObservableProperty] private string _discoveryText = "Looking for Exchange servers in Active Directory…";

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (State.IsDemo)
        {
            Discovered.Clear();
            Discovered.Add(new DiscoveredServer("EX01", "ex01.contoso.com", "Exchange SE (15.2.2562)", "London", true));
            Discovered.Add(new DiscoveredServer("EX02", "ex02.contoso.com", "Exchange SE (15.2.2562)", "London", true));
            DiscoveryText = "Found 2 Exchange servers in Active Directory. Pick one, or type another.";
            return;
        }
        DiscoveryText = "Looking for Exchange servers in Active Directory…";
        try
        {
            var servers = await ExchangeDiscovery.FindAsync();
            Discovered.Clear();
            foreach (var s in servers) Discovered.Add(s);
            if (servers.Count == 0)
            {
                DiscoveryText = "No Exchange servers found in Active Directory. Type the server's FQDN.";
                return;
            }
            DiscoveryText = $"Found {servers.Count} Exchange server{(servers.Count == 1 ? "" : "s")} in Active Directory" +
                            (servers.Any(s => s.InThisSite) ? " (this site's listed first)" : "") + ". Pick one, or type another.";
            Log.Info($"Exchange servers in AD: {string.Join(", ", servers.Select(s => s.Fqdn))}");
            // Fill the box if it's empty (an Exchange server running the app, or the last server used, wins)
            if (string.IsNullOrWhiteSpace(Server)) Server = servers[0].Fqdn;
        }
        catch (Exception ex)
        {
            DiscoveryText = "Couldn't look Exchange servers up in Active Directory (is this machine domain-joined?). Type the server's FQDN.";
            Log.Warn($"Exchange server lookup in AD failed: {ex.Message}");
        }
    }

    public override string Title => "Connect to Exchange";
    public override string Subtitle => "Connects to the Exchange PowerShell endpoint over Kerberos. Run it on an Exchange server or a domain-joined admin machine.";

    public string? LocalServer { get; }
    public bool IsOnExchangeServer => LocalServer is not null;
    public string LocalServerText => LocalServer is null
        ? "This machine isn't an Exchange server, so you'll connect remotely."
        : $"This machine is an Exchange server ({LocalServer}).";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] private string _server;
    [ObservableProperty] private bool _useCurrentUser = true;
    [ObservableProperty] private string _userName;

    /// <summary>Set from the PasswordBox in the view.</summary>
    public string Password { private get; set; } = "";

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Server);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var ok = await State.RunAsync($"Connecting to {Server}", async () =>
        {
            PSCredential? credential = null;
            if (!UseCurrentUser)
            {
                if (string.IsNullOrWhiteSpace(UserName) || Password.Length == 0)
                    throw new InvalidOperationException("Enter a user name and password, or use your current sign-in.");
                credential = new PSCredential(UserName.Trim(), CertificateService.ToSecure(Password));
            }

            var session = await ExchangeSession.ConnectAsync(Server, credential);
            State.Session?.Dispose();
            State.Session = session;
            State.Settings.LastServer = Server.Trim();
            State.Settings.Save();

            State.SetBusyText("Reading servers and certificates");
            await State.RefreshAsync();
        });

        if (ok) Main.Navigate("inventory");
    }

    [RelayCommand]
    private void Disconnect()
    {
        State.Session?.Dispose();
        State.Session = null;
        State.Servers.Clear();
        State.Inventory.Clear();
        Log.Info("Disconnected");
    }
}
