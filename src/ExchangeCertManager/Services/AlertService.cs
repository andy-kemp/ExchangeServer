using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

public enum AlertDelivery { ExchangeSmtp, Smtp2Go }

/// <summary>
/// Expiry alert settings, in ProgramData\ExchangeCertManager\alerts.json (Administrators and SYSTEM only), so the
/// nightly task can read them. Nothing secret is in here: the SMTP2Go API key is stored separately with DPAPI.
/// </summary>
public sealed class AlertSettings
{
    public static readonly int[] DefaultReminderDays = { 60, 30, 14, 7, 3, 1 };

    public bool Enabled { get; set; }
    public List<string> Recipients { get; set; } = new();
    public List<int> ReminderDays { get; set; } = DefaultReminderDays.ToList();
    public AlertDelivery Delivery { get; set; } = AlertDelivery.ExchangeSmtp;
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; } = 25;
    public bool SmtpStartTls { get; set; } = true;
    /// <summary>Sign in to SMTP as the Windows account that's running (the task's account at night, you for a test).</summary>
    public bool SmtpUseWindowsAccount { get; set; }
    public string From { get; set; } = "";
    /// <summary>The Exchange server the nightly check connects to.</summary>
    public string ConnectServer { get; set; } = "";
    public bool AlertRenewalFailures { get; set; } = true;

    private static string FilePath => Path.Combine(AppPaths.ProgramData, "alerts.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AlertSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read {FilePath}: {ex.Message}");
            return new();
        }
    }

    public void Save()
    {
        AppPaths.EnsureSecureDirectory(AppPaths.ProgramData);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>Which reminder each certificate last had, so each window is only emailed once.</summary>
public sealed class AlertStateEntry
{
    public string Name { get; set; } = "";
    /// <summary>The reminder window last sent (days); 0 means it was expired.</summary>
    public int Window { get; set; }
    public DateTime LastSent { get; set; }
}

/// <summary>One certificate, as the Alerts page and the email show it.</summary>
public sealed record CertificateAlertRow(string Name, string Thumbprint, string Servers, string Services, DateTime? Expires, int DaysLeft,
    bool IsAuth, string Status, string NextReminder)
{
    public string State => DaysLeft < 0 ? "Error" : DaysLeft <= 14 ? "Warning" : DaysLeft <= 60 ? "Info" : "Ok";
    public string DaysText => DaysLeft < 0 ? $"Expired {-DaysLeft} day{(DaysLeft == -1 ? "" : "s")} ago" : $"{DaysLeft} day{(DaysLeft == 1 ? "" : "s")}";
}

public sealed record AlertMessage(string Subject, string Text, string Html);

/// <summary>
/// Certificate expiry alerts: which certificates to watch, which reminder is due, the email, and sending it through
/// Exchange (SMTP) or SMTP2Go. Every reminder and failure also goes to the Windows Application event log, so any
/// monitoring that watches it sees them too.
/// </summary>
public static class AlertService
{
    private static readonly string StateFile = Path.Combine(AppPaths.ProgramData, "alerts-state.json");
    private static readonly string Smtp2GoKeyFile = Path.Combine(AppPaths.ProgramData, "smtp2go.key");
    private const string Smtp2GoEndpoint = "https://api.smtp2go.com/v3/email/send";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // ---------------------------------------------------------------- SMTP2Go key (DPAPI, like the Cloudflare token)

    public static bool HasSmtp2GoKey => File.Exists(Smtp2GoKeyFile);

    public static void SaveSmtp2GoKey(string key)
    {
        AppPaths.EnsureSecureDirectory(AppPaths.ProgramData);
        File.WriteAllBytes(Smtp2GoKeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.LocalMachine));
        Log.Success("SMTP2Go API key saved (encrypted, Administrators and SYSTEM only)");
    }

    private static string? LoadSmtp2GoKey() =>
        File.Exists(Smtp2GoKeyFile)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Smtp2GoKeyFile), null, DataProtectionScope.LocalMachine))
            : null;

    public static void DeleteSmtp2GoKey()
    {
        if (File.Exists(Smtp2GoKeyFile)) File.Delete(Smtp2GoKeyFile);
        Log.Info("SMTP2Go API key removed");
    }

    // ---------------------------------------------------------------- which certificates, which reminder

    /// <summary>
    /// The certificates worth an alert: in use (assigned to a service) or the Auth certificate, and not already
    /// replaced by a later certificate in use that covers the same names.
    /// </summary>
    public static List<CertificateGroup> Watched(IEnumerable<CertificateGroup> groups)
    {
        var all = groups.Where(g => !g.IsPending && g.NotAfter is not null).ToList();
        var inUse = all.Where(g => g.IsAuthCertificate || g.HasServices).ToList();
        return inUse.Where(g => g.IsAuthCertificate || !inUse.Any(h =>
                !ReferenceEquals(h, g) && h.HasServices && h.NotAfter > g.NotAfter &&
                g.Domains.Count > 0 && g.Domains.All(d => Covers(h.Domains, d))))
            .ToList();
    }

    private static bool Covers(IEnumerable<string> names, string name) => names.Any(n =>
        n.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (n.StartsWith("*.", StringComparison.Ordinal) && name.Contains('.') &&
         name[(name.IndexOf('.') + 1)..].Equals(n[2..], StringComparison.OrdinalIgnoreCase)));

    /// <summary>The reminder window a certificate is in: 0 if it has expired, null if it's outside every window.</summary>
    public static int? WindowFor(int daysLeft, IEnumerable<int> reminderDays)
    {
        if (daysLeft < 0) return 0;
        var inside = reminderDays.Where(w => daysLeft <= w).ToList();
        return inside.Count == 0 ? null : inside.Min();
    }

    private static string NameOf(CertificateGroup g) =>
        g.IsAuthCertificate ? "Exchange Auth (OAuth) certificate"
        : g.CommonName is { Length: > 0 } cn ? cn
        : g.FriendlyName is { Length: > 0 } fn ? fn : g.Thumbprint;

    public static List<CertificateAlertRow> Describe(IEnumerable<CertificateGroup> groups, AlertSettings settings)
    {
        var windows = settings.ReminderDays.Where(d => d > 0).OrderByDescending(d => d).ToList();
        return Watched(groups).OrderBy(g => g.NotAfter).Select(g =>
        {
            var days = g.DaysLeft ?? 0;
            string next;
            if (days < 0) next = "Daily until it's replaced";
            else
            {
                // The next window it hasn't reached yet
                var current = WindowFor(days, windows);
                var upcoming = windows.Where(w => w < days && (current is null || w < current)).DefaultIfEmpty(-1).Max();
                next = current is null && windows.Count > 0
                    ? $"In {days - windows[0]} days ({windows[0]} days before expiry)"
                    : upcoming >= 0 ? $"In {days - upcoming} day{(days - upcoming == 1 ? "" : "s")} ({upcoming} days before expiry)"
                    : "When it expires";
            }
            var services = string.Join("; ", g.Installs.Where(i => i.HasServices).Select(i => i.Services).Distinct());
            return new CertificateAlertRow(NameOf(g), g.Thumbprint, g.ServersText, g.IsAuthCertificate ? "Auth" : services,
                g.NotAfter, days, g.IsAuthCertificate, g.ExpiryText, next);
        }).ToList();
    }

    public static Dictionary<string, AlertStateEntry> LoadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<Dictionary<string, AlertStateEntry>>(File.ReadAllText(StateFile)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveState(Dictionary<string, AlertStateEntry> state)
    {
        AppPaths.EnsureSecureDirectory(AppPaths.ProgramData);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The certificates due a reminder now, and the state to save once it's been sent. A certificate gets one
    /// reminder per window (60, 30, 14… days); once expired, one a day until it's replaced.
    /// </summary>
    public static (List<CertificateAlertRow> Due, Dictionary<string, AlertStateEntry> NewState) Evaluate(
        IEnumerable<CertificateGroup> groups, AlertSettings settings, Dictionary<string, AlertStateEntry> state, DateTime now)
    {
        var rows = Describe(groups, settings);
        var newState = new Dictionary<string, AlertStateEntry>(StringComparer.OrdinalIgnoreCase);
        var due = new List<CertificateAlertRow>();
        foreach (var row in rows)
        {
            var window = WindowFor(row.DaysLeft, settings.ReminderDays);
            state.TryGetValue(row.Thumbprint, out var last);
            if (window is null)
            {
                continue; // outside every window: nothing to remember
            }
            var send = last is null || window < last.Window || (window == 0 && last.LastSent.Date < now.Date);
            if (send)
            {
                due.Add(row);
                newState[row.Thumbprint] = new AlertStateEntry { Name = row.Name, Window = window.Value, LastSent = now };
            }
            else newState[row.Thumbprint] = last!;
        }
        return (due, newState);
    }

    // ---------------------------------------------------------------- the email

    public static AlertMessage BuildMessage(IReadOnlyList<CertificateAlertRow> due, IReadOnlyList<string> problems,
        IReadOnlyList<CertificateAlertRow> all, AlertSettings settings)
    {
        string subject;
        if (due.Count == 0) subject = problems.Count == 1 ? $"Exchange certificates: {Shorten(problems[0])}" : "Exchange certificates: problems to look at";
        else if (due.Count == 1)
        {
            var d = due[0];
            subject = d.DaysLeft < 0 ? $"Expired: Exchange certificate {d.Name}"
                : $"Exchange certificate {d.Name} expires in {d.DaysText}";
        }
        else subject = $"{due.Count} Exchange certificates need attention";

        var org = settings.ConnectServer;
        var text = new StringBuilder();
        text.AppendLine($"Exchange Certificate Manager checked the certificates through {org} at {DateTime.Now:d MMMM yyyy HH:mm}.");
        text.AppendLine();
        if (due.Count > 0)
        {
            text.AppendLine("Needs attention:");
            foreach (var d in due) text.AppendLine($"- {Describe(d)}");
            text.AppendLine();
        }
        if (problems.Count > 0)
        {
            text.AppendLine("Problems:");
            foreach (var p in problems) text.AppendLine($"- {p}");
            text.AppendLine();
        }
        var others = all.Where(a => due.All(d => d.Thumbprint != a.Thumbprint) && a.DaysLeft <= settings.ReminderDays.DefaultIfEmpty(60).Max()).ToList();
        if (others.Count > 0)
        {
            text.AppendLine("Also coming up (already reminded):");
            foreach (var o in others) text.AppendLine($"- {o.Name}: {(o.DaysLeft < 0 ? "expired" : $"{o.DaysText} left")} ({o.Expires:d MMM yyyy})");
            text.AppendLine();
        }
        text.AppendLine("To renew, open Exchange Certificate Manager. Let's Encrypt certificates renew themselves 30 days before expiry; anything else needs a new request, or a PFX to import.");
        text.AppendLine($"Reminders are sent {string.Join(", ", settings.ReminderDays.OrderByDescending(x => x))} days before expiry, and daily once a certificate has expired. Change them on the Alerts page.");

        var h = WebUtility.HtmlEncode;
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f1f1f;max-width:680px\">");
        html.Append("<div style=\"border-top:4px solid #8A4E2E;padding-top:12px;font-weight:600;font-size:16px;color:#8A4E2E\">Exchange Certificate Manager</div>");
        html.Append($"<p>Checked the certificates through {h(org)} at {h(DateTime.Now.ToString("d MMMM yyyy HH:mm"))}.</p>");
        if (due.Count > 0)
        {
            html.Append("<p style=\"font-weight:600;margin-bottom:4px\">Needs attention</p><table style=\"border-collapse:collapse;width:100%\">");
            html.Append("<tr style=\"text-align:left;color:#6b6b6b;font-size:12px\"><th style=\"padding:6px 8px\">CERTIFICATE</th><th style=\"padding:6px 8px\">EXPIRES</th><th style=\"padding:6px 8px\">SERVERS</th><th style=\"padding:6px 8px\">SERVICES</th></tr>");
            foreach (var d in due)
            {
                var colour = d.DaysLeft < 0 ? "#B3261E" : d.DaysLeft <= 14 ? "#A15C00" : "#1f1f1f";
                html.Append($"<tr style=\"border-top:1px solid #e5e5e5\"><td style=\"padding:6px 8px\">{h(d.Name)}<br><span style=\"font-family:Consolas,monospace;font-size:11px;color:#6b6b6b\">{h(d.Thumbprint)}</span></td>");
                html.Append($"<td style=\"padding:6px 8px;color:{colour};font-weight:600\">{h(d.Expires?.ToString("d MMM yyyy") ?? "")}<br>{h(d.DaysLeft < 0 ? d.DaysText : $"{d.DaysText} left")}</td>");
                html.Append($"<td style=\"padding:6px 8px\">{h(d.Servers)}</td><td style=\"padding:6px 8px\">{h(d.Services)}</td></tr>");
            }
            html.Append("</table>");
        }
        if (problems.Count > 0)
        {
            html.Append("<p style=\"font-weight:600;margin-bottom:4px;color:#B3261E\">Problems</p><ul>");
            foreach (var p in problems) html.Append($"<li>{h(p)}</li>");
            html.Append("</ul>");
        }
        if (others.Count > 0)
        {
            html.Append("<p style=\"font-weight:600;margin-bottom:4px\">Also coming up (already reminded)</p><ul>");
            foreach (var o in others) html.Append($"<li>{h(o.Name)}: {h(o.DaysLeft < 0 ? "expired" : $"{o.DaysText} left")} ({h(o.Expires?.ToString("d MMM yyyy") ?? "")})</li>");
            html.Append("</ul>");
        }
        html.Append("<p style=\"color:#6b6b6b;font-size:12px\">To renew, open Exchange Certificate Manager. Let's Encrypt certificates renew themselves 30 days before expiry; anything else needs a new request, or a PFX to import. ");
        html.Append($"Reminders are sent {h(string.Join(", ", settings.ReminderDays.OrderByDescending(x => x)))} days before expiry, and daily once a certificate has expired.</p></div>");

        return new AlertMessage(subject, text.ToString(), html.ToString());
    }

    private static string Describe(CertificateAlertRow d) =>
        $"{d.Name} {(d.DaysLeft < 0 ? $"EXPIRED on {d.Expires:d MMM yyyy} ({d.DaysText})" : $"expires on {d.Expires:d MMM yyyy} ({d.DaysText} left)")}. " +
        $"Servers: {d.Servers}.{(d.Services.Length > 0 ? $" Services: {d.Services}." : "")} Thumbprint {d.Thumbprint}.";

    private static string Shorten(string s) => s.Length <= 70 ? s : s[..67] + "…";

    public static AlertMessage TestMessage(AlertSettings settings)
    {
        var text = $"This is a test from Exchange Certificate Manager on {Environment.MachineName}, sent as {WindowsIdentity.GetCurrent().Name}.\n\n" +
                   $"Expiry reminders will come from here {string.Join(", ", settings.ReminderDays.OrderByDescending(x => x))} days before a certificate expires.";
        var html = $"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\"><div style=\"border-top:4px solid #8A4E2E;padding-top:12px;font-weight:600;color:#8A4E2E\">Exchange Certificate Manager</div><p>{WebUtility.HtmlEncode(text).Replace("\n", "<br>")}</p></div>";
        return new AlertMessage("Test: Exchange certificate alerts", text, html);
    }

    // ---------------------------------------------------------------- sending

    public static async Task SendAsync(AlertSettings settings, AlertMessage message, CancellationToken ct = default)
    {
        var to = settings.Recipients.Where(r => r.Contains('@')).ToList();
        if (to.Count == 0) throw new InvalidOperationException("There are no recipients to send to.");
        if (!settings.From.Contains('@')) throw new InvalidOperationException("Set the From address first.");
        if (settings.Delivery == AlertDelivery.Smtp2Go) await SendSmtp2GoAsync(settings, to, message, ct);
        else await SendSmtpAsync(settings, to, message, ct);
        Log.Success($"Alert email sent to {string.Join(", ", to)}: {message.Subject}");
    }

    private static async Task SendSmtp2GoAsync(AlertSettings settings, List<string> to, AlertMessage message, CancellationToken ct)
    {
        var key = LoadSmtp2GoKey() ?? throw new InvalidOperationException("No SMTP2Go API key is saved. Add one on the Alerts page.");
        using var req = new HttpRequestMessage(HttpMethod.Post, Smtp2GoEndpoint)
        {
            Content = JsonContent.Create(new
            {
                sender = $"Exchange Certificate Manager <{settings.From}>",
                to,
                subject = message.Subject,
                text_body = message.Text,
                html_body = message.Html,
            }),
        };
        req.Headers.Add("X-Smtp2go-Api-Key", key);
        req.Headers.UserAgent.ParseAdd($"ExchangeCertManager/{UpdateService.CurrentText}");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("error", out var e)) error = e.GetString();
                else if (data.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Number && f.GetInt32() > 0)
                    error = data.TryGetProperty("failures", out var fl) ? $"some recipients failed: {fl}" : "some recipients failed";
            }
        }
        catch { /* not JSON: use the status code */ }
        if (!resp.IsSuccessStatusCode || error is not null)
            throw new InvalidOperationException($"SMTP2Go: {error ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}"}. " +
                                                "Check the API key can send, and that the From address is a verified sender in SMTP2Go.");
    }

    private static async Task SendSmtpAsync(AlertSettings settings, List<string> to, AlertMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpServer)) throw new InvalidOperationException("Set the SMTP server first.");
        try
        {
            await SendSmtpOnceAsync(settings, to, message, settings.SmtpStartTls, ct);
        }
        catch (Exception ex) when (settings.SmtpStartTls && !settings.SmtpUseWindowsAccount && IsTlsFailure(ex))
        {
            // The usual reason is the very thing we're warning about: an expired or mismatched certificate on the
            // SMTP service. An expiry reminder isn't sensitive, so it's sent without TLS rather than not at all.
            Log.Warn($"SMTP: TLS to {settings.SmtpServer} failed ({Inner(ex).Message}), so the alert is sent without it");
            await SendSmtpOnceAsync(settings, to, message, false, ct);
        }
    }

    private static async Task SendSmtpOnceAsync(AlertSettings settings, List<string> to, AlertMessage message, bool tls, CancellationToken ct)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(settings.From, "Exchange Certificate Manager"),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        foreach (var r in to) mail.To.Add(r);
        mail.Body = message.Text;
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, Encoding.UTF8, "text/html"));
        using var client = new SmtpClient(settings.SmtpServer.Trim(), settings.SmtpPort)
        {
            EnableSsl = tls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 60_000,
            UseDefaultCredentials = settings.SmtpUseWindowsAccount,
        };
        if (!settings.SmtpUseWindowsAccount) client.Credentials = null;
        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpException ex)
        {
            throw new InvalidOperationException(ExplainSmtp(ex, settings), ex);
        }
    }

    private static Exception Inner(Exception ex) { while (ex.InnerException is not null) ex = ex.InnerException; return ex; }

    private static bool IsTlsFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return true;
            if (e.Message.Contains("secure connection", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Exchange's SMTP refusals, in plain words with what to do about them.</summary>
    private static string ExplainSmtp(SmtpException ex, AlertSettings settings)
    {
        var raw = Inner(ex).Message;
        var all = ex.Message + " " + raw;
        if (all.Contains("5.7.1", StringComparison.Ordinal) || all.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || all.Contains("permission", StringComparison.OrdinalIgnoreCase))
            return $"Exchange refused it ({ex.Message.Trim()}). Exchange doesn't accept anonymous mail that claims to come from one of its own domains, " +
                   "or relay to outside addresses. Either tick 'Sign in with the Windows account' (the task's account needs a mailbox, or Send As on the From address), " +
                   "use a From address outside your accepted domains, or add a receive connector that lets this server relay.";
        if (all.Contains("5.7.57", StringComparison.Ordinal) || all.Contains("5.7.3", StringComparison.Ordinal))
            return $"Exchange wanted a signed-in client ({ex.Message.Trim()}). Tick 'Sign in with the Windows account', or use port 25 instead of 587.";
        if (ex.StatusCode == SmtpStatusCode.GeneralFailure || raw.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return $"Couldn't reach {settings.SmtpServer}:{settings.SmtpPort} ({raw}). Check the server name and that the port is open from this machine.";
        return $"SMTP: {ex.Message.Trim()}{(raw != ex.Message ? $" ({raw})" : "")}";
    }
}

/// <summary>The Windows Application event log, so existing monitoring picks up reminders and failures.</summary>
public static class AlertEventLog
{
    public const string Source = "Exchange Certificate Manager";
    public const int CertificateExpiring = 1001, CertificateExpired = 1002, RenewalFailed = 1003, CheckFailed = 1004, SendFailed = 1005, Sent = 1010;

    public static void Write(int id, EventLogEntryType type, string message)
    {
        try
        {
            if (!EventLog.SourceExists(Source)) EventLog.CreateEventSource(Source, "Application");
            EventLog.WriteEntry(Source, message, type, id);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't write to the event log: {ex.Message}");
        }
    }
}

/// <summary>Run by the nightly task after any Let's Encrypt renewals.</summary>
public static class AlertRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<string> renewalProblems)
    {
        var settings = AlertSettings.Load();
        foreach (var p in renewalProblems) AlertEventLog.Write(AlertEventLog.RenewalFailed, EventLogEntryType.Error, p);
        if (!settings.Enabled) return 0;

        Log.Info("Checking certificate expiry for alerts");
        var problems = settings.AlertRenewalFailures ? renewalProblems.ToList() : new List<string>();
        var groups = new List<CertificateGroup>();
        if (settings.ConnectServer.Length == 0)
        {
            problems.Add("Expiry alerts have no Exchange server to check. Open the Alerts page and save the settings while connected.");
        }
        else
        {
            try
            {
                using var session = await ExchangeSession.ConnectAsync(settings.ConnectServer, credential: null);
                var certs = new CertificateService(session);
                var (found, failed) = await certs.GetInventoryAsync(await certs.GetServersAsync());
                groups = found;
                foreach (var s in failed) problems.Add($"Couldn't read the certificates on {s}, so they weren't checked.");
            }
            catch (Exception ex)
            {
                problems.Add($"Couldn't check the certificates through {settings.ConnectServer} as {WindowsIdentity.GetCurrent().Name}: {ex.Message}");
            }
        }
        foreach (var p in problems.Where(p => !renewalProblems.Contains(p))) AlertEventLog.Write(AlertEventLog.CheckFailed, EventLogEntryType.Error, p);

        var all = AlertService.Describe(groups, settings);
        var (due, newState) = AlertService.Evaluate(groups, settings, AlertService.LoadState(), DateTime.Now);
        foreach (var d in due)
        {
            var expired = d.DaysLeft < 0;
            AlertEventLog.Write(expired ? AlertEventLog.CertificateExpired : AlertEventLog.CertificateExpiring,
                expired ? EventLogEntryType.Error : EventLogEntryType.Warning,
                $"{d.Name} {(expired ? "has expired" : $"expires in {d.DaysText}")} ({d.Expires:d MMM yyyy}). Servers: {d.Servers}. Thumbprint {d.Thumbprint}.");
        }

        if (due.Count == 0 && problems.Count == 0)
        {
            Log.Info($"No certificate is due a reminder ({all.Count} watched)");
            if (groups.Count > 0) AlertService.SaveState(newState);
            return 0;
        }

        if (settings.Recipients.Count == 0)
        {
            Log.Warn("Expiry alerts have no recipients, so only the event log was written");
            if (groups.Count > 0) AlertService.SaveState(newState);
            return problems.Count == 0 ? 0 : 1;
        }

        try
        {
            await AlertService.SendAsync(settings, AlertService.BuildMessage(due, problems, all, settings));
            if (groups.Count > 0) AlertService.SaveState(newState); // only once it's sent, so a failed send is retried tomorrow
            AlertEventLog.Write(AlertEventLog.Sent, EventLogEntryType.Information,
                $"Expiry alert sent to {string.Join(", ", settings.Recipients)} ({due.Count} certificate(s), {problems.Count} problem(s)).");
            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't send the expiry alert: {ex.Message}");
            AlertEventLog.Write(AlertEventLog.SendFailed, EventLogEntryType.Error, $"Couldn't send the expiry alert: {ex.Message}");
            return 1;
        }
    }
}
