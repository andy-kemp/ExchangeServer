using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using Microsoft.Win32;

namespace ExchangeCertManager.ViewModels;

public sealed partial class RequestViewModel : PageViewModel
{
    public RequestViewModel(MainViewModel main) : base(main)
    {
        var s = State.Settings;
        _organisation = s.Organisation;
        _city = s.City;
        _county = s.County;
        _country = s.Country;
    }

    public override string Title => "New or renewal request";
    public override string Subtitle => "Creates the key pair on the Exchange server and gives you a CSR to send to your CA. Complete it on the Complete request page when the CA replies.";

    public IReadOnlyList<int> KeySizes { get; } = new[] { 2048, 4096 };

    [ObservableProperty] private ExchangeServerInfo? _server;
    [ObservableProperty] private bool _isWildcard;
    [ObservableProperty] private string _commonName = "";
    [ObservableProperty] private string _domainsText = "";
    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private int _keySize = 2048;
    [ObservableProperty] private string _organisation;
    [ObservableProperty] private string _organisationalUnit = "";
    [ObservableProperty] private string _city;
    [ObservableProperty] private string _county;
    [ObservableProperty] private string _country;
    [ObservableProperty] private string _warning = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCsr))]
    [NotifyCanExecuteChangedFor(nameof(CopyCsrCommand), nameof(SaveCsrCommand))]
    private string _csr = "";

    public bool HasCsr => Csr.Length > 0;

    public override void OnShown()
    {
        Server ??= State.Servers.FirstOrDefault(s => s.Fqdn.Equals(State.Session?.ConnectedServer, StringComparison.OrdinalIgnoreCase))
                   ?? State.Servers.FirstOrDefault();
    }

    partial void OnIsWildcardChanged(bool value)
    {
        if (value && !CommonName.StartsWith("*.", StringComparison.Ordinal))
        {
            var parts = CommonName.Split('.');
            CommonName = parts.Length > 2 ? "*." + string.Join(".", parts.Skip(1)) : "*." + CommonName;
        }
        if (!value && CommonName.StartsWith("*.", StringComparison.Ordinal)) CommonName = "mail." + CommonName[2..];
    }

    partial void OnCommonNameChanged(string value)
    {
        if (FriendlyName.Length == 0 || FriendlyName.StartsWith("Exchange ", StringComparison.Ordinal))
            FriendlyName = $"Exchange {value.Replace("*.", "wildcard ")} {DateTime.Now:yyyy}";
    }

    /// <summary>Pre-fills from an existing certificate so a renewal keeps the same names.</summary>
    public void PrefillFrom(CertificateGroup g)
    {
        Csr = "";
        Server = State.Servers.FirstOrDefault(s => g.Installs.Any(i => i.Server.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                 ?? State.Servers.FirstOrDefault();
        IsWildcard = g.CommonName.StartsWith("*.", StringComparison.Ordinal);
        CommonName = g.CommonName;
        DomainsText = string.Join(Environment.NewLine, g.Domains.Where(d => !d.Equals(g.CommonName, StringComparison.OrdinalIgnoreCase)));
        FriendlyName = $"{g.FriendlyName} (renewed {DateTime.Now:yyyy-MM})".Trim();
        Warning = "";
        Log.Info($"Renewal prefilled from {g.Thumbprint}");
    }

    [RelayCommand]
    private Task SuggestAsync() => State.RunAsync("Reading the configured URLs", async () =>
    {
        if (Server is null) return;
        var names = await SuggestNamesAsync(Server.Name);
        if (names.Count == 0)
        {
            AppState.Inform("No URLs are configured on the virtual directories yet. Set the internal and external URLs first, then try again.");
            return;
        }
        if (IsWildcard)
        {
            var root = string.Join(".", PickCommonName(names).Split('.').Skip(1));
            CommonName = $"*.{root}";
            DomainsText = string.Join(Environment.NewLine, names.Where(n => !HealthService.Covers(new[] { CommonName }, n)));
        }
        else
        {
            CommonName = PickCommonName(names);
            DomainsText = string.Join(Environment.NewLine, names.Where(n => n != CommonName));
        }
        Warning = InternalNameWarning(names.Append(CommonName)) ?? "";
    });

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (Server is null) { AppState.Inform("Pick the server to create the request on."); return; }
        var cn = CommonName.Trim().ToLowerInvariant();
        var domains = SplitNames(DomainsText);
        var all = domains.Prepend(cn).ToList();

        if (IsWildcard && !cn.StartsWith("*.", StringComparison.Ordinal)) { AppState.Inform("A wildcard subject must start with *. (for example *.contoso.com)."); return; }
        if (ValidateNames(all, IsWildcard) is { } error) { AppState.Inform(error); return; }
        if (string.IsNullOrWhiteSpace(FriendlyName)) { AppState.Inform("Give the certificate a friendly name."); return; }
        if (Country.Trim().Length is not (0 or 2)) { AppState.Inform("Country must be a two-letter code, for example GB."); return; }

        // A wildcard CSR should also carry the bare domain so contoso.com itself is covered.
        if (IsWildcard && !domains.Contains(cn[2..])) domains.Add(cn[2..]);

        Warning = InternalNameWarning(all) ?? "";
        var pending = State.Inventory.SelectMany(g => g.Installs).Where(i => i.IsPending && i.Server == Server.Name).ToList();
        if (pending.Count > 0 &&
            !AppState.Confirm($"{Server.Name} already has {pending.Count} pending request(s). Create another one anyway? (Old ones can be removed from the Certificates page.)"))
            return;

        await State.RunAsync("Creating the request", async () =>
        {
            var request = new CsrRequest(Server.Name, cn, domains, FriendlyName.Trim(), KeySize,
                Organisation, OrganisationalUnit, City, County, Country.Trim().ToUpperInvariant());
            Csr = await State.Certs!.NewRequestAsync(request);

            var s = State.Settings;
            s.Organisation = Organisation; s.City = City; s.County = County; s.Country = Country;
            s.Save();
            await State.RefreshAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(HasCsr))]
    private void CopyCsr()
    {
        Clipboard.SetText(Csr);
        Log.Info("CSR copied to the clipboard");
    }

    [RelayCommand(CanExecute = nameof(HasCsr))]
    private void SaveCsr()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save certificate request",
            Filter = "Certificate request (*.req)|*.req|Text (*.txt)|*.txt",
            FileName = $"{CommonName.Replace("*", "wildcard")}-{DateTime.Now:yyyyMMdd}.req",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        File.WriteAllText(dialog.FileName, Csr);
        Log.Success($"CSR saved to {dialog.FileName}");
    }
}
