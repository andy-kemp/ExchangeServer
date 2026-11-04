using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeCertManager.Helpers;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

/// <summary>
/// The one-click fixes offered by the health check. Each follows Microsoft's documented procedure:
/// "Maintain the Exchange Server OAuth certificate" for the Auth certificate, and
/// TlsCertificateName = "&lt;I&gt;issuer&lt;S&gt;subject" for connectors.
/// </summary>
public sealed class FixService
{
    public const string AuthSubject = "cn=Microsoft Exchange Server Auth Certificate";
    public const string AuthFriendlyName = "Microsoft Exchange Server Auth Certificate";

    /// <summary>Microsoft recommends at least 48 hours for a new Auth certificate to reach every server.</summary>
    public static readonly TimeSpan StagingDelay = TimeSpan.FromHours(49);

    private readonly ExchangeSession _session;
    private readonly CertificateService _certs;

    public FixService(ExchangeSession session, CertificateService certs)
    {
        _session = session;
        _certs = certs;
    }

    // ---------------------------------------------------------------- Auth certificate

    /// <summary>
    /// Creates a new Auth certificate on <paramref name="server"/>, copies it to the other servers and sets it as the
    /// next Auth certificate. Staged: it becomes active automatically after 49 hours. Immediate: it's published now,
    /// the previous one is cleared and the OWA/ECP app pools are recycled.
    /// </summary>
    public async Task<string> RenewAuthCertificateAsync(string server, IReadOnlyList<ExchangeServerInfo> allServers, bool immediate, IProgress<string>? progress = null)
    {
        progress?.Report($"Creating the new Auth certificate on {server}");
        // -Services None: the default (IMAP, POP, SMTP) prompts to replace the default SMTP certificate, which can't be
        // answered over remoting, and the Auth certificate doesn't need any services.
        var created = await _session.InvokeAsync("New-ExchangeCertificate", new Dictionary<string, object?>
        {
            ["Server"] = server,
            ["KeySize"] = 2048,
            ["PrivateKeyExportable"] = true,
            ["SubjectName"] = AuthSubject,
            ["FriendlyName"] = AuthFriendlyName,
            ["DomainName"] = Array.Empty<string>(),
            ["Services"] = "None",
        });
        var thumb = created.Select(o => o.Str("Thumbprint")).FirstOrDefault(t => t.Length > 0)
                    ?? throw new InvalidOperationException("Exchange didn't return the new certificate's thumbprint.");
        Log.Success($"New Auth certificate {thumb}");

        var others = allServers.Select(s => s.Name).Where(s => !s.Equals(server, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            // Exchange distributes it on its own, but that can take a while; copying it now means every server has it
            // before it becomes active.
            await _certs.DeployAsync(server, thumb, others, Array.Empty<string>(), progress);
        }

        var effective = immediate ? DateTime.Now : DateTime.Now.Add(StagingDelay);
        progress?.Report(immediate ? "Making it the Auth certificate now" : "Staging it as the next Auth certificate");
        await _session.InvokeAsync("Set-AuthConfig", new Dictionary<string, object?>
        {
            ["NewCertificateThumbprint"] = thumb,
            ["NewCertificateEffectiveDate"] = effective,
            ["Force"] = null, // the "less than 48 hours" warning, when immediate
            ["Confirm"] = false,
        });

        if (immediate)
        {
            await _session.InvokeAsync("Set-AuthConfig", new Dictionary<string, object?> { ["PublishCertificate"] = null, ["Force"] = null, ["Confirm"] = false });
            await _session.InvokeAsync("Set-AuthConfig", new Dictionary<string, object?> { ["ClearPreviousCertificate"] = null, ["Force"] = null, ["Confirm"] = false });
            await RecycleAuthAppPoolsAsync(allServers, progress);
        }
        else
        {
            Log.Info($"The new Auth certificate becomes active automatically from {effective:dd MMM yyyy HH:mm}");
        }
        return thumb;
    }

    public async Task ClearPreviousAuthCertificateAsync(IReadOnlyList<ExchangeServerInfo> allServers, IProgress<string>? progress = null)
    {
        progress?.Report("Clearing the previous Auth certificate");
        await _session.InvokeAsync("Set-AuthConfig", new Dictionary<string, object?> { ["ClearPreviousCertificate"] = null, ["Force"] = null, ["Confirm"] = false });
        await RecycleAuthAppPoolsAsync(allServers, progress);
    }

    /// <summary>
    /// OWA and ECP cache the Auth certificate. Recycling their app pools needs a normal (not Exchange) PowerShell
    /// session on each server over WinRM; if that isn't available the commands are logged to run by hand.
    /// </summary>
    public async Task RecycleAuthAppPoolsAsync(IEnumerable<ExchangeServerInfo> servers, IProgress<string>? progress = null)
    {
        foreach (var info in servers)
        {
            var server = info.Fqdn; // WinRM and Kerberos want the real host name
            progress?.Report($"Recycling the OWA and ECP app pools on {server}");
            try
            {
                await ServerShell.RunAsync(server, _session.Credential,
                    "Import-Module WebAdministration; Restart-WebAppPool MSExchangeOWAAppPool; Restart-WebAppPool MSExchangeECPAppPool");
                Log.Success($"Recycled MSExchangeOWAAppPool and MSExchangeECPAppPool on {server}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't recycle the app pools on {server} ({ex.Message}). On {server}, in an elevated PowerShell window, run: " +
                         "Restart-WebAppPool MSExchangeOWAAppPool; Restart-WebAppPool MSExchangeECPAppPool");
            }
        }
    }

    // ---------------------------------------------------------------- connectors

    public static string TlsNameFor(CertificateGroup cert) => $"<I>{cert.Issuer}<S>{cert.Subject}";

    /// <summary>
    /// The best replacement for a connector's pinned certificate: CA-issued, valid, on every relevant server,
    /// preferring the same subject name, then whichever is assigned to SMTP, then the longest-lived.
    /// </summary>
    public static CertificateGroup? PickConnectorCertificate(IEnumerable<CertificateGroup> inventory, HealthFix fix)
    {
        var oldCn = CertificateGroup.SubjectCn(ParseSubject(fix.OldTlsName ?? ""));
        return inventory
            .Where(g => !g.IsSelfSigned && !g.IsPending && g.DaysLeft is > 0)
            .Where(g => fix.Servers.Count == 0 || fix.Servers.All(s => g.Installs.Any(i => i.Server.Equals(s, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(g => oldCn.Length > 0 && g.CommonName.Equals(oldCn, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => g.Installs.Any(i => i.Services.Contains("SMTP", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(g => g.NotAfter)
            .FirstOrDefault();
    }

    public async Task UpdateConnectorAsync(HealthFix fix, CertificateGroup cert)
    {
        var command = fix.ConnectorType == "Send" ? "Set-SendConnector" : "Set-ReceiveConnector";
        await _session.InvokeAsync(command, new Dictionary<string, object?>
        {
            ["Identity"] = fix.ConnectorIdentity,
            ["TlsCertificateName"] = TlsNameFor(cert),
        });
        Log.Success($"{fix.ConnectorType} connector '{fix.ConnectorName}' now uses {cert.CommonName} ({cert.Thumbprint})");
    }

    private static string ParseSubject(string tls)
    {
        var s = tls.IndexOf("<S>", StringComparison.OrdinalIgnoreCase);
        return s < 0 ? tls : tls[(s + 3)..];
    }
}

/// <summary>A normal Windows PowerShell session to a server over WinRM (port 5985, Kerberos), for the few things the
/// Exchange endpoint can't do, such as recycling app pools.</summary>
public static class ServerShell
{
    public static async Task RunAsync(string server, PSCredential? credential, string script)
    {
        var info = new WSManConnectionInfo(new Uri($"http://{server}:5985/wsman"), "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", credential)
        {
            AuthenticationMechanism = AuthenticationMechanism.Kerberos,
            OpenTimeout = 30_000,
            OperationTimeout = 5 * 60_000,
        };
        await Task.Run(() =>
        {
            using var runspace = RunspaceFactory.CreateRunspace(info);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(script);
            ps.Invoke();
            if (ps.Streams.Error.Count > 0)
                throw new InvalidOperationException(string.Join(" | ", ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString())));
        });
    }
}
