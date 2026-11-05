using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

/// <summary>
/// Certificate expiry alerts: who gets them, how they're sent, and when. The nightly task (the same one that renews
/// Let's Encrypt certificates) does the checking, so alerts work for certificates from any CA.
/// </summary>
public sealed partial class AlertsViewModel : PageViewModel
{
    public AlertsViewModel(MainViewModel main) : base(main)
    {
        State.InventoryChanged += (_, _) => RefreshRows();
    }

    public override string Title => "Alerts";
    public override string Subtitle => "Email reminders before any Exchange certificate expires, from any CA, plus Let's Encrypt renewal failures. The nightly task checks every server and also writes to the Windows event log.";

    public ObservableCollection<CertificateAlertRow> Rows { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _recipientsText = "";
    [ObservableProperty] private string _reminderDaysText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseExchange), nameof(UseSmtp2Go))]
    private AlertDelivery _delivery = AlertDelivery.ExchangeSmtp;
    [ObservableProperty] private string _smtpServer = "";
    [ObservableProperty] private string _smtpPort = "25";
    [ObservableProperty] private bool _smtpStartTls = true;
    [ObservableProperty] private bool _smtpUseWindowsAccount;
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private bool _alertRenewalFailures = true;
    [ObservableProperty] private string _smtp2GoStatus = "";
    [ObservableProperty] private string _taskStatus = "";
    [ObservableProperty] private string _taskState = "Info";
    [ObservableProperty] private string _summary = "";

    public bool UseExchange { get => Delivery == AlertDelivery.ExchangeSmtp; set { if (value) Delivery = AlertDelivery.ExchangeSmtp; } }
    public bool UseSmtp2Go { get => Delivery == AlertDelivery.Smtp2Go; set { if (value) Delivery = AlertDelivery.Smtp2Go; } }

    /// <summary>Set from the PasswordBox in the view; never stored in settings or logged.</summary>
    public string Smtp2GoKeyInput { private get; set; } = "";
    public event EventHandler? Smtp2GoKeySaved;

    private AlertSettings _settings = new();

    public override void OnShown()
    {
        if (State.IsDemo) return;
        _settings = AlertSettings.Load();
        Enabled = _settings.Enabled;
        RecipientsText = string.Join(Environment.NewLine, _settings.Recipients);
        ReminderDaysText = string.Join(", ", _settings.ReminderDays.OrderByDescending(d => d));
        Delivery = _settings.Delivery;
        SmtpServer = _settings.SmtpServer.Length > 0 ? _settings.SmtpServer : State.Session?.ConnectedServer ?? "";
        SmtpPort = _settings.SmtpPort.ToString();
        SmtpStartTls = _settings.SmtpStartTls;
        SmtpUseWindowsAccount = _settings.SmtpUseWindowsAccount;
        From = _settings.From;
        AlertRenewalFailures = _settings.AlertRenewalFailures;
        RefreshSmtp2GoStatus();
        RefreshTask();
        RefreshRows();
    }

    /// <summary>What's watched, from the certificates already read on the Certificates page.</summary>
    public void RefreshRows()
    {
        var days = ParseDays(ReminderDaysText) ?? AlertSettings.DefaultReminderDays.ToList();
        Rows.Clear();
        foreach (var r in AlertService.Describe(State.Inventory, new AlertSettings { ReminderDays = days })) Rows.Add(r);
        var soon = Rows.Count(r => r.DaysLeft <= days.DefaultIfEmpty(60).Max());
        Summary = Rows.Count == 0
            ? "No certificates in use found yet. Open Certificates and refresh."
            : $"{Rows.Count} certificate{(Rows.Count == 1 ? "" : "s")} watched; {soon} within {days.DefaultIfEmpty(60).Max()} days of expiry. Certificates that a newer one has replaced are left out.";
    }

    private void RefreshSmtp2GoStatus() =>
        Smtp2GoStatus = AlertService.HasSmtp2GoKey
            ? "An SMTP2Go API key is saved on this machine (encrypted; Administrators and SYSTEM only)."
            : "No SMTP2Go API key saved yet.";

    [RelayCommand]
    private void RefreshTask()
    {
        var info = State.IsDemo ? null : RenewalTask.Info();
        if (info is null)
        {
            TaskState = "Warning";
            TaskStatus = "The nightly task isn't set up on this machine yet, so nothing will be checked. Set it up on the Let's Encrypt page: the same task renews Let's Encrypt certificates and checks for alerts. You don't need any Let's Encrypt certificates to use it.";
            return;
        }
        var stale = info.LastRun is { } last && DateTime.Now - last > TimeSpan.FromDays(2);
        TaskState = stale || info.LastResult is not (0 or 0x41303) ? "Warning" : "Ok";
        TaskStatus = $"The nightly task runs as {info.UserId}. " +
                     (info.LastRun is { } l ? $"Last run {l:d MMM HH:mm}: {RenewalTask.ResultText(info.LastResult)}. " : "It hasn't run yet. ") +
                     (info.NextRun is { } n ? $"Next run {n:d MMM HH:mm}." : "") +
                     (stale ? " It hasn't run for more than two days, so alerts aren't being checked: look at it in Task Scheduler." : "");
    }

    [RelayCommand]
    private void SetUpTask()
    {
        Main.Navigate("acme");
        AppState.Inform("Set up the nightly task at the bottom of this page (5 Automatic renewal). It does the expiry check for alerts too, so register it even if you don't use Let's Encrypt.");
    }

    [RelayCommand]
    private void RunTaskNow()
    {
        if (State.IsDemo) { AppState.Inform("Nothing runs in demo mode."); return; }
        if (!RenewalTask.Exists()) { SetUpTask(); return; }
        if (!AppState.Confirm("Run the nightly task now, as its own account? It renews anything due and sends any reminders that are due. Reminders already sent aren't sent again.")) return;
        try { RenewalTask.RunNow(); }
        catch (Exception ex) { AppState.Inform($"Couldn't start the task: {ex.Message}"); return; }
        Log.Info("Started the nightly task. Refresh in a minute to see the result.");
    }

    private static List<int>? ParseDays(string text)
    {
        var parts = text.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var days = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var d) || d < 1 || d > 365) return null;
            days.Add(d);
        }
        return days.Count == 0 ? null : days.Distinct().OrderByDescending(d => d).ToList();
    }

    /// <summary>Checks the form and builds the settings, or says what's wrong.</summary>
    private AlertSettings? Build(out string? error)
    {
        error = null;
        var recipients = SplitNames(RecipientsText);
        var days = ParseDays(ReminderDaysText);
        if (days is null) { error = "Reminder days should be whole numbers from 1 to 365, separated by commas: for example 60, 30, 14, 7, 3, 1."; return null; }
        if (recipients.Any(r => !r.Contains('@') || r.StartsWith('@') || r.EndsWith('@'))) { error = "Each recipient should be an email address."; return null; }
        if (Enabled && recipients.Count == 0) { error = "Add at least one recipient."; return null; }
        if (Enabled && !From.Contains('@')) { error = "Set the From address the alerts come from."; return null; }
        if (!int.TryParse(SmtpPort, out var port) || port is < 1 or > 65535) { error = "The SMTP port should be a number, usually 25 or 587."; return null; }
        if (Enabled && UseExchange && string.IsNullOrWhiteSpace(SmtpServer)) { error = "Set the SMTP server: an Exchange server's name."; return null; }
        if (Enabled && UseSmtp2Go && !AlertService.HasSmtp2GoKey) { error = "Save an SMTP2Go API key first."; return null; }

        return new AlertSettings
        {
            Enabled = Enabled,
            Recipients = recipients,
            ReminderDays = days,
            Delivery = Delivery,
            SmtpServer = SmtpServer.Trim(),
            SmtpPort = port,
            SmtpStartTls = SmtpStartTls,
            SmtpUseWindowsAccount = SmtpUseWindowsAccount,
            From = From.Trim(),
            AlertRenewalFailures = AlertRenewalFailures,
            ConnectServer = State.Session?.ConnectedServer is { Length: > 0 } s ? s : _settings.ConnectServer,
        };
    }

    [RelayCommand]
    private void Save()
    {
        if (State.IsDemo) { AppState.Inform("Nothing is saved in demo mode."); return; }
        var settings = Build(out var error);
        if (settings is null) { AppState.Inform(error!); return; }
        try
        {
            settings.Save();
            _settings = settings;
            Log.Success(settings.Enabled
                ? $"Expiry alerts on: {string.Join(", ", settings.Recipients)}, {string.Join(", ", settings.ReminderDays)} days before expiry, by {(settings.Delivery == AlertDelivery.Smtp2Go ? "SMTP2Go" : $"SMTP through {settings.SmtpServer}")}"
                : "Expiry alerts off");
            RefreshRows();
            RefreshTask();
            if (settings.Enabled && !RenewalTask.Exists())
                AppState.Inform("Saved. Now set up the nightly task (Set up the task), or nothing will be checked.");
        }
        catch (Exception ex)
        {
            AppState.Inform($"Couldn't save: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SendTestAsync()
    {
        if (State.IsDemo) { AppState.Inform("Nothing is sent in demo mode."); return; }
        var wasEnabled = Enabled;
        Enabled = true; // check the form as if alerts were on
        var settings = Build(out var error);
        Enabled = wasEnabled;
        if (settings is null) { AppState.Inform(error!); return; }
        var ok = await State.RunAsync("Sending a test email", () => AlertService.SendAsync(settings, AlertService.TestMessage(settings)));
        if (ok)
        {
            var note = UseExchange && SmtpUseWindowsAccount
                ? "\n\nThis was sent as you. At night it's sent as the task's account, so use Run task now to check that works too."
                : "";
            AppState.Inform($"Test email sent to {string.Join(", ", settings.Recipients)}. Check it arrived (and isn't in junk).{note}");
        }
    }

    [RelayCommand]
    private void SaveSmtp2GoKey()
    {
        if (State.IsDemo) { AppState.Inform("Nothing is saved in demo mode."); return; }
        if (string.IsNullOrWhiteSpace(Smtp2GoKeyInput)) { AppState.Inform("Paste the SMTP2Go API key first."); return; }
        try
        {
            AlertService.SaveSmtp2GoKey(Smtp2GoKeyInput);
            Smtp2GoKeyInput = "";
            Smtp2GoKeySaved?.Invoke(this, EventArgs.Empty);
            RefreshSmtp2GoStatus();
            AppState.Inform("Saved. Use Send test email to check it works with your From address.");
        }
        catch (Exception ex)
        {
            AppState.Inform($"Couldn't save the key: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveSmtp2GoKey()
    {
        if (!AlertService.HasSmtp2GoKey) return;
        if (!AppState.Confirm("Remove the saved SMTP2Go API key from this machine?")) return;
        AlertService.DeleteSmtp2GoKey();
        RefreshSmtp2GoStatus();
    }

    /// <summary>For the demo and screenshots.</summary>
    public void LoadDemo()
    {
        Enabled = true;
        RecipientsText = "exchange-admins@contoso.com\nandy@contoso.com";
        ReminderDaysText = "60, 30, 14, 7, 3, 1";
        Delivery = AlertDelivery.Smtp2Go;
        SmtpServer = "ex01.ad.contoso.com";
        From = "certificates@alerts.contoso.com";
        Smtp2GoStatus = "An SMTP2Go API key is saved on this machine (encrypted; Administrators and SYSTEM only).";
        TaskState = "Ok";
        TaskStatus = $"The nightly task runs as CONTOSO\\gMSA_ExchCert$. Last run {DateTime.Today.AddHours(3).AddMinutes(41):d MMM HH:mm}: succeeded. Next run {DateTime.Today.AddDays(1).AddHours(3).AddMinutes(17):d MMM HH:mm}.";
        RefreshRows();
    }
}
