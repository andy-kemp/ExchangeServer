using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using ExchangeCertManager.Views;

namespace ExchangeCertManager.ViewModels;

public sealed partial class AcmeServerRow : ObservableObject
{
    public AcmeServerRow(ExchangeServerInfo server)
    {
        Server = server;
        _webroot = AcmeService.DefaultWebroot(server);
    }

    public ExchangeServerInfo Server { get; }
    public string Name => Server.Name;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _webroot;
}

public sealed partial class AcmeViewModel : PageViewModel
{
    public AcmeViewModel(MainViewModel main) : base(main)
    {
        _email = State.Settings.AcmeEmail;
        _taskUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        RefreshCloudflareStatus();
    }

    public override string Title => "Let's Encrypt";
    public override string Subtitle => "Issue a free certificate from Let's Encrypt, install it on your servers and renew it automatically. Port 80 must reach Exchange for the HTTP challenge; use DNS (Cloudflare can do it for you) for wildcards or when port 80 isn't open.";

    public ObservableCollection<AcmeServerRow> Servers { get; } = new();
    public ObservableCollection<AcmeRenewal> Renewals { get; } = new();
    public ServiceSelection Services { get; } = new();

    [ObservableProperty] private string _email;
    [ObservableProperty] private bool _staging = true;
    [ObservableProperty] private string _domainsText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseHttp), nameof(UseDns), nameof(UseCloudflare), nameof(CanRenewUnattended))]
    private AcmeChallengeType _challenge = AcmeChallengeType.Http01;
    [ObservableProperty] private bool _removeOld = true;
    [ObservableProperty] private bool _saveForRenewal = true;
    [ObservableProperty] private string _taskUser;
    [ObservableProperty] private string _taskStatus = "";
    [ObservableProperty] private AcmeRenewal? _selectedRenewal;

    // One bool per radio button
    public bool UseHttp { get => Challenge == AcmeChallengeType.Http01; set { if (value) Challenge = AcmeChallengeType.Http01; } }
    public bool UseDns { get => Challenge == AcmeChallengeType.Dns01Manual; set { if (value) Challenge = AcmeChallengeType.Dns01Manual; } }
    public bool UseCloudflare { get => Challenge == AcmeChallengeType.Dns01Cloudflare; set { if (value) Challenge = AcmeChallengeType.Dns01Cloudflare; } }
    private bool IsDns => Challenge != AcmeChallengeType.Http01;
    /// <summary>Manual DNS needs a person; HTTP and Cloudflare renew on their own.</summary>
    public bool CanRenewUnattended => Challenge != AcmeChallengeType.Dns01Manual;

    // ---- Cloudflare token
    [ObservableProperty] private string _cloudflareStatus = "";
    /// <summary>Set from the PasswordBox in the view; never stored in settings or logged.</summary>
    public string CloudflareTokenInput { private get; set; } = "";
    public event EventHandler? CloudflareTokenSaved;

    private void RefreshCloudflareStatus() =>
        CloudflareStatus = CloudflareDns.HasToken
            ? "A Cloudflare token is saved on this machine (encrypted; Administrators and SYSTEM only)."
            : "No Cloudflare token saved yet.";

    [RelayCommand]
    private async Task SaveCloudflareTokenAsync()
    {
        if (State.IsDemo) { AppState.Inform("Nothing is saved in demo mode."); return; }
        var token = CloudflareTokenInput.Trim();
        if (token.Length == 0) { AppState.Inform("Paste the Cloudflare API token first."); return; }
        try
        {
            CloudflareStatus = "Checking the token with Cloudflare…";
            var zones = await new CloudflareDns(token).VerifyAsync();
            CloudflareDns.SaveToken(token);
            CloudflareTokenSaved?.Invoke(this, EventArgs.Empty);
            CloudflareStatus = zones.Count == 0
                ? "Token saved, but it can't see any zones yet. Check it has Zone > DNS > Edit on your zone."
                : $"Token saved. It can edit: {string.Join(", ", zones)}.";
        }
        catch (Exception ex)
        {
            CloudflareStatus = $"The token wasn't saved: {ex.Message}";
            Log.Warn($"Cloudflare token check failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveCloudflareToken()
    {
        if (!CloudflareDns.HasToken) return;
        if (!AppState.Confirm("Remove the saved Cloudflare token? Renewals that use Cloudflare will fail until you add one again.")) return;
        CloudflareDns.DeleteToken();
        RefreshCloudflareStatus();
    }

    /// <summary>Set from the PasswordBox in the view.</summary>
    public string TaskPassword { private get; set; } = "";

    public override void OnShown()
    {
        if (Servers.Count != State.Servers.Count)
        {
            Servers.Clear();
            foreach (var s in State.Servers) Servers.Add(new AcmeServerRow(s));
        }
        ReloadRenewals();
    }

    private void ReloadRenewals()
    {
        Renewals.Clear();
        foreach (var r in AcmeService.LoadRenewals()) Renewals.Add(r);
        TaskStatus = RenewalTask.Exists()
            ? $"The daily renewal task is registered. It renews anything within {RenewalRunner.RenewWithinDays} days of expiry."
            : "No renewal task is registered yet.";
    }

    [RelayCommand]
    private Task SuggestAsync() => State.RunAsync("Reading the configured URLs", async () =>
    {
        var server = Servers.FirstOrDefault(s => s.IsSelected)?.Name ?? State.Servers.FirstOrDefault()?.Name;
        if (server is null) return;
        var names = (await SuggestNamesAsync(server))
            .Where(n => !n.EndsWith(".local") && !n.EndsWith(".lan") && !n.EndsWith(".internal") && !n.EndsWith(".corp"))
            .ToList();
        var cn = PickCommonName(names);
        DomainsText = string.Join(Environment.NewLine, names.OrderBy(n => n == cn ? 0 : 1));
    });

    [RelayCommand]
    private async Task IssueAsync()
    {
        var domains = SplitNames(DomainsText);
        var rows = Servers.Where(s => s.IsSelected).ToList();
        var services = Services.Selected();

        if (domains.Count == 0) { AppState.Inform("Add the names the certificate should cover."); return; }
        if (ValidateNames(domains, allowWildcard: true) is { } error) { AppState.Inform(error); return; }
        if (InternalNameWarning(domains) is { } internalWarning) { AppState.Inform(internalWarning); return; }
        if (!Email.Contains('@')) { AppState.Inform("Let's Encrypt needs a contact email for expiry notices."); return; }
        if (rows.Count == 0) { AppState.Inform("Pick at least one server to install it on."); return; }
        if (!IsDns && domains.Any(d => d.StartsWith("*."))) { AppState.Inform("Wildcard names need the DNS challenge."); return; }
        if (!IsDns && rows.Any(r => string.IsNullOrWhiteSpace(r.Webroot))) { AppState.Inform("Every server needs a web root for the HTTP challenge."); return; }
        if (UseCloudflare && !CloudflareDns.HasToken) { AppState.Inform("Save a Cloudflare API token under 3 Validation first."); return; }

        var mode = Staging ? "STAGING (test certificate, not trusted by browsers)" : "PRODUCTION";
        if (!AppState.Confirm(
                $"Request a {mode} certificate for:\n{string.Join("\n", domains)}\n\n" +
                $"Install on {string.Join(", ", rows.Select(r => r.Name))} and assign {(services.Count == 0 ? "no services" : string.Join(", ", services))}?",
                "Let's Encrypt"))
            return;

        var def = new AcmeRenewal
        {
            Domains = domains,
            Email = Email.Trim(),
            Staging = Staging,
            ConnectServer = State.Session!.ConnectedServer,
            Servers = rows.Select(r => r.Name).ToList(),
            Webroots = rows.ToDictionary(r => r.Name, r => r.Webroot.Trim()),
            Services = services,
            RemoveOldCertificate = RemoveOld,
            Challenge = Challenge,
            LastThumbprint = AcmeService.LoadRenewals()
                .FirstOrDefault(r => r.Domains.OrderBy(d => d).SequenceEqual(domains.OrderBy(d => d), StringComparer.OrdinalIgnoreCase))?.LastThumbprint,
        };

        State.Settings.AcmeEmail = def.Email;
        State.Settings.Save();

        var acme = new AcmeService { ConfirmDnsRecords = ShowDnsRecordsAsync };
        var ok = await State.RunAsync("Talking to Let's Encrypt", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            await AcmeDeployer.IssueAndDeployAsync(State.Certs!, acme, def, Challenge, progress);
            if (SaveForRenewal && CanRenewUnattended) AcmeService.SaveRenewal(def);
            State.SetBusyText("Refreshing");
            await State.RefreshAsync();
        });

        if (ok)
        {
            ReloadRenewals();
            var next = Staging
                ? "That was a staging certificate. Untick Staging and run it again for a trusted one."
                : SaveForRenewal && CanRenewUnattended
                    ? "Saved for automatic renewal. Register the daily task below if you haven't already."
                    : UseDns ? "Manual DNS certificates can't renew unattended, so put a reminder in for 60 days (or use Cloudflare)." : "";
            AppState.Inform($"Certificate installed. {next}");
        }
    }

    private static Task ShowDnsRecordsAsync(IReadOnlyList<(string Name, string Value)> records)
    {
        var ok = Application.Current.Dispatcher.Invoke(() => DnsRecordsWindow.Show(records));
        if (!ok) throw new OperationCanceledException();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RegisterTask()
    {
        if (string.IsNullOrWhiteSpace(TaskUser) || TaskPassword.Length == 0)
        {
            AppState.Inform("Enter the account and password the task should run as. It needs Organization Management (or the Exchange certificate role) and local admin on this machine.");
            return;
        }

        var exe = Environment.ProcessPath ?? "";
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (exe.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) &&
            !AppState.Confirm($"The task will run the app from {exe}. If you move or delete it, renewals stop. Copy it somewhere permanent (for example C:\\ExchangeCertManager) first?\n\nRegister anyway?"))
            return;

        try
        {
            RenewalTask.Register(TaskUser.Trim(), CertificateService.ToSecure(TaskPassword));
            ReloadRenewals();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't register the task: {ex.Message}");
            AppState.Inform($"Couldn't register the task: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RenewNowAsync()
    {
        if (SelectedRenewal is not { } def) return;
        if (!AppState.Confirm($"Renew {def.DomainsText} now, even if it isn't due?")) return;
        var ok = await State.RunAsync("Renewing", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            await AcmeDeployer.IssueAndDeployAsync(State.Certs!, new AcmeService { ConfirmDnsRecords = ShowDnsRecordsAsync }, def, def.Challenge, progress);
            AcmeService.SaveRenewal(def);
            await State.RefreshAsync();
        });
        if (ok) ReloadRenewals();
    }

    [RelayCommand]
    private void DeleteRenewal()
    {
        if (SelectedRenewal is not { } def) return;
        if (!AppState.Confirm($"Stop renewing {def.DomainsText}? The installed certificate stays where it is.")) return;
        var all = AcmeService.LoadRenewals();
        all.RemoveAll(r => r.Id == def.Id);
        AcmeService.SaveRenewals(all);
        Log.Info($"Removed renewal for {def.DomainsText}");
        ReloadRenewals();
    }
}
