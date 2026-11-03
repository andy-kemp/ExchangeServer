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
