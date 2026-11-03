using ExchangeCertManager.Helpers;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

/// <summary>
/// The checks that catch people out in real Exchange estates: expiring certificates in use,
/// the Exchange Auth (OAuth) certificate, SANs that don't cover the configured URLs,
/// connectors pinned to a certificate that's gone, and clutter.
/// </summary>
public sealed class HealthService
{
    private readonly ExchangeSession _session;
    private readonly CertificateService _certs;

    public HealthService(ExchangeSession session, CertificateService certs)
    {
        _session = session;
        _certs = certs;
    }

    public async Task<List<HealthItem>> RunAsync(IReadOnlyList<ExchangeServerInfo> servers, IProgress<string>? progress = null)
    {
        var items = new List<HealthItem>();
        progress?.Report("Reading certificates");
        var (groups, failed) = await _certs.GetInventoryAsync(servers);
        foreach (var f in failed)
            items.Add(new(HealthSeverity.Error, "Connectivity", f, "Couldn't read certificates", "Check the server is up and you have Organization Management rights."));

        CheckExpiry(groups, items);

        progress?.Report("Checking the Exchange Auth certificate");
        await CheckAuthCertificateAsync(groups, servers, items);

        foreach (var server in servers.Where(s => !failed.Contains(s.Name)))
        {
            progress?.Report($"Checking URLs on {server.Name}");
            await CheckHostnameCoverageAsync(server, groups, items);

            progress?.Report($"Checking connectors on {server.Name}");
            await CheckReceiveConnectorsAsync(server, groups, items);
        }

        progress?.Report("Checking send connectors");
        await CheckSendConnectorsAsync(groups, items);

        CheckConsistency(groups, servers.Where(s => !failed.Contains(s.Name)).ToList(), items);
        CheckClutter(groups, items);

        if (!items.Any(i => i.Severity >= HealthSeverity.Warning))
            items.Insert(0, new(HealthSeverity.Ok, "Summary", "", "No problems found", $"{groups.Count} certificates checked across {servers.Count} servers."));

        return items.OrderByDescending(i => i.Severity).ThenBy(i => i.Area).ThenBy(i => i.Server).ToList();
    }

    private static void CheckExpiry(List<CertificateGroup> groups, List<HealthItem> items)
    {
        foreach (var g in groups.Where(g => !g.IsPending && (g.HasServices || g.IsAuthCertificate)))
        {
            if (g.IsAuthCertificate) continue; // reported by the Auth check
            if (g.DaysLeft is not { } days) continue;
            var where = g.ServicesText;
            if (days < 0)
                items.Add(new(HealthSeverity.Error, "Expiry", g.ServersText, $"{g.CommonName} has expired",
                    $"Expired {-days} days ago and is still assigned ({where}). Renew it and deploy the replacement."));
            else if (days < 30)
                items.Add(new(HealthSeverity.Error, "Expiry", g.ServersText, $"{g.CommonName} expires in {days} days",
                    $"Assigned to {where}. Renew now."));
            else if (days < 60)
                items.Add(new(HealthSeverity.Warning, "Expiry", g.ServersText, $"{g.CommonName} expires in {days} days",
                    $"Assigned to {where}. Plan the renewal."));
        }
    }

    private async Task CheckAuthCertificateAsync(List<CertificateGroup> groups, IReadOnlyList<ExchangeServerInfo> servers, List<HealthItem> items)
    {
        var (current, next, nextEffective) = await _certs.GetAuthConfigAsync();
        if (string.IsNullOrEmpty(current))
        {
            items.Add(new(HealthSeverity.Warning, "Auth certificate", "", "No Exchange Auth certificate is configured",
                "Hybrid, OWA/ECP sign-in and OAuth depend on it. Create one with New-ExchangeCertificate -KeySize 2048 -SubjectName \"cn=Microsoft Exchange Server Auth Certificate\" -DomainName @() -Services SMTP, then Set-AuthConfig."));
            return;
        }

        var group = groups.FirstOrDefault(g => g.Thumbprint.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            items.Add(new(HealthSeverity.Error, "Auth certificate", "", "The Auth certificate isn't installed on any server",
                $"Get-AuthConfig points at {current}, which none of the checked servers have."));
            return;
        }

        var missing = servers.Select(s => s.Name)
            .Where(n => !group.Installs.Any(i => i.Server.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
            items.Add(new(HealthSeverity.Error, "Auth certificate", string.Join(", ", missing), "Auth certificate missing on some servers",
                "Every Mailbox server needs the Auth certificate. Use Deploy to copy it across (no services needed)."));

        var days = group.DaysLeft ?? 999;
        var nextNote = next is null ? "No next certificate is staged." : $"Next certificate {next} is staged{(nextEffective is { } d ? $" for {d:d}" : "")}.";
        if (days < 0)
            items.Add(new(HealthSeverity.Error, "Auth certificate", "", "The Exchange Auth certificate has expired",
                $"Hybrid free/busy, OWA sign-in and OAuth will fail. Renew it with the Exchange Auth certificate renewal steps (MRA script or Set-AuthConfig), then run the Hybrid Configuration Wizard. {nextNote}"));
        else if (days < 60)
            items.Add(new(HealthSeverity.Warning, "Auth certificate", "", $"The Exchange Auth certificate expires in {days} days",
                $"Renew before it expires and re-run the Hybrid Configuration Wizard if you're hybrid. {nextNote}"));
        else
            items.Add(new(HealthSeverity.Ok, "Auth certificate", "", $"Exchange Auth certificate valid for {days} days", nextNote));
    }

    private async Task CheckHostnameCoverageAsync(ExchangeServerInfo server, List<CertificateGroup> groups, List<HealthItem> items)
    {
        var iisCert = groups.FirstOrDefault(g => !g.IsSelfSigned && g.Installs.Any(i =>
            i.Server.Equals(server.Name, StringComparison.OrdinalIgnoreCase) &&
            i.Services.Contains("IIS", StringComparison.OrdinalIgnoreCase)));

        if (iisCert is null)
        {
            items.Add(new(HealthSeverity.Error, "Client access", server.Name, "No CA-issued certificate is assigned to IIS",
                "Clients will get certificate warnings. Assign a public or internal CA certificate to IIS."));
            return;
        }

        var hosts = await _certs.GetClientHostnamesAsync(server.Name);
        var uncovered = hosts.Where(h => !Covers(iisCert.Domains, h)).ToList();
        if (uncovered.Count > 0)
            items.Add(new(HealthSeverity.Error, "Client access", server.Name, $"{iisCert.CommonName} doesn't cover all configured URLs",
                $"Not on the certificate: {string.Join(", ", uncovered)}. Either add these names at renewal or change the virtual directory URLs."));
        else if (hosts.Count > 0)
            items.Add(new(HealthSeverity.Ok, "Client access", server.Name, "Certificate covers every configured URL",
                $"{iisCert.CommonName} covers {string.Join(", ", hosts)}."));
    }

    private async Task CheckReceiveConnectorsAsync(ExchangeServerInfo server, List<CertificateGroup> groups, List<HealthItem> items)
    {
        try
        {
            var connectors = await _session.InvokeAsync("Get-ReceiveConnector",
                new Dictionary<string, object?> { ["Server"] = server.Name }, quiet: true);
            foreach (var c in connectors)
            {
                var tls = c.Str("TlsCertificateName");
                if (tls.Length == 0) continue;
                CheckPinnedCertificate($"Receive connector '{c.Str("Name")}'", server.Name, tls,
                    groups.Where(g => g.Installs.Any(i => i.Server.Equals(server.Name, StringComparison.OrdinalIgnoreCase))).ToList(), items);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Get-ReceiveConnector on {server.Name}: {ex.Message}");
        }
    }

    private async Task CheckSendConnectorsAsync(List<CertificateGroup> groups, List<HealthItem> items)
    {
        try
        {
            foreach (var c in await _session.InvokeAsync("Get-SendConnector", quiet: true))
            {
                var tls = c.Str("TlsCertificateName");
                if (tls.Length == 0) continue;
                CheckPinnedCertificate($"Send connector '{c.Str("Name")}'", c.Str("SourceTransportServers"), tls, groups, items);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Get-SendConnector: {ex.Message}");
        }
    }

    /// <summary>
    /// TlsCertificateName is "&lt;I&gt;issuer&lt;S&gt;subject". After a renewal with a different issuer
    /// or subject the connector silently stops matching, which breaks hybrid mail flow.
    /// </summary>
    private static void CheckPinnedCertificate(string what, string server, string tls, List<CertificateGroup> candidates, List<HealthItem> items)
    {
        var (issuer, subject) = ParseTlsName(tls);
        var matches = candidates.Where(g => !g.IsPending &&
                                            Normalise(g.Issuer) == Normalise(issuer) &&
                                            Normalise(g.Subject) == Normalise(subject)).ToList();
        if (matches.Count == 0)
        {
            items.Add(new(HealthSeverity.Error, "Mail flow", server, $"{what} points at a certificate that isn't installed",
                $"TlsCertificateName is {tls}. Update it with Set-ReceiveConnector/Set-SendConnector -TlsCertificateName \"<I>issuer<S>subject\" for the new certificate. Hybrid connectors are the usual culprit."));
            return;
        }

        var best = matches.OrderByDescending(m => m.NotAfter).First();
        if (best.DaysLeft is < 30)
            items.Add(new(HealthSeverity.Warning, "Mail flow", server, $"{what} uses a certificate expiring in {best.DaysLeft} days",
                "When you renew, keep the same issuer and subject, or update TlsCertificateName straight after."));
    }

    private static (string Issuer, string Subject) ParseTlsName(string tls)
    {
        var i = tls.IndexOf("<I>", StringComparison.OrdinalIgnoreCase);
        var s = tls.IndexOf("<S>", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || s < 0 || s < i) return ("", tls);
        return (tls.Substring(i + 3, s - i - 3), tls[(s + 3)..]);
    }

    private static string Normalise(string dn) =>
        string.Join(",", dn.Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0));

    private static void CheckConsistency(List<CertificateGroup> groups, List<ExchangeServerInfo> servers, List<HealthItem> items)
    {
        if (servers.Count < 2) return;
        foreach (var g in groups.Where(g => !g.IsSelfSigned && !g.IsPending && g.HasServices && !g.IsAuthCertificate))
        {
            var missing = servers.Where(s => !g.Installs.Any(i => i.Server.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.Name).ToList();
            if (missing.Count > 0)
                items.Add(new(HealthSeverity.Warning, "Consistency", string.Join(", ", missing), $"{g.CommonName} isn't on every server",
                    "Behind a load balancer, clients hitting these servers get a different certificate. Use Deploy to copy it across."));
        }
    }

    private static void CheckClutter(List<CertificateGroup> groups, List<HealthItem> items)
    {
        foreach (var g in groups.Where(g => !g.IsPending && !g.HasServices && !g.IsAuthCertificate && g.DaysLeft is < 0))
            items.Add(new(HealthSeverity.Info, "Clean-up", g.ServersText, $"Expired, unused: {g.CommonName}",
                $"Thumbprint {g.Thumbprint}. Safe to remove once you've checked nothing references it."));

        foreach (var server in groups.SelectMany(g => g.Installs).Where(i => i.IsPending).GroupBy(i => i.Server))
            items.Add(new(HealthSeverity.Info, "Clean-up", server.Key, $"{server.Count()} pending request(s)",
                "Complete them with the CA response, or remove them if they were abandoned."));
    }

    /// <summary>True if any SAN covers the hostname. Wildcards match exactly one label.</summary>
    public static bool Covers(IEnumerable<string> sans, string host)
    {
        foreach (var san in sans)
        {
            if (san.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
            if (san.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = san[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    !host[..^suffix.Length].Contains('.') && host.Length > suffix.Length)
                    return true;
            }
        }
        return false;
    }
}
