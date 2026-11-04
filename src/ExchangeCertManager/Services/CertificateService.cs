using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ExchangeCertManager.Helpers;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

public sealed record CsrRequest(
    string Server,
    string CommonName,
    IReadOnlyList<string> Domains,
    string FriendlyName,
    int KeySize,
    string? Organisation = null,
    string? OrganisationalUnit = null,
    string? City = null,
    string? County = null,
    string? Country = null);

/// <summary>All certificate operations, each one a single command against the Exchange endpoint.</summary>
public sealed class CertificateService
{
    private readonly ExchangeSession _session;

    public CertificateService(ExchangeSession session) => _session = session;

    public static readonly string[] AllServices = { "IIS", "SMTP", "IMAP", "POP" };

    // ---------------------------------------------------------------- discovery

    public async Task<List<ExchangeServerInfo>> GetServersAsync()
    {
        var results = await _session.InvokeAsync("Get-ExchangeServer");
        return results
            .Where(o => !o.Bool("IsEdgeServer")) // Edge isn't reachable over this endpoint
            .Select(ExchangeServerInfo.From)
            .OrderBy(s => s.Name)
            .ToList();
    }

    public async Task<List<ServerCertificate>> GetCertificatesAsync(string server)
    {
        var results = await _session.InvokeAsync("Get-ExchangeCertificate", new Dictionary<string, object?> { ["Server"] = server });
        return results.Select(o => ServerCertificate.From(server, o)).ToList();
    }

    public async Task<(List<CertificateGroup> Groups, List<string> FailedServers)> GetInventoryAsync(IEnumerable<ExchangeServerInfo> servers)
    {
        var all = new List<ServerCertificate>();
        var failed = new List<string>();
        foreach (var s in servers)
        {
            try { all.AddRange(await GetCertificatesAsync(s.Name)); }
            catch (Exception ex)
            {
                failed.Add(s.Name);
                Log.Error($"Couldn't read certificates on {s.Name}: {ex.Message}");
            }
        }

        var auth = await GetAuthConfigAsync();
        var groups = all
            .GroupBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CertificateGroup
            {
                Thumbprint = g.Key,
                Installs = g.OrderBy(i => i.Server).ToList(),
                IsAuthCertificate = auth.Current?.Equals(g.Key, StringComparison.OrdinalIgnoreCase) == true
                                    || auth.Next?.Equals(g.Key, StringComparison.OrdinalIgnoreCase) == true,
            })
            .OrderBy(g => g.IsPending)
            .ThenBy(g => g.IsSelfSigned)
            .ThenBy(g => g.NotAfter)
            .ToList();
        return (groups, failed);
    }

    public async Task<(string? Current, string? Next, DateTime? NextEffective)> GetAuthConfigAsync()
    {
        try
        {
            var r = (await _session.InvokeAsync("Get-AuthConfig", quiet: true)).FirstOrDefault();
            if (r is null) return (null, null, null);
            var next = r.Str("NextCertificateThumbprint");
            return (r.Str("CurrentCertificateThumbprint"), string.IsNullOrEmpty(next) ? null : next, r.Date("NextCertificateEffectiveDate"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read the Exchange Auth configuration: {ex.Message}");
            return (null, null, null);
        }
    }

    public async Task<string?> GetPreviousAuthThumbprintAsync()
    {
        try
        {
            var r = (await _session.InvokeAsync("Get-AuthConfig", quiet: true)).FirstOrDefault();
            var prev = r?.Str("PreviousCertificateThumbprint");
            return string.IsNullOrEmpty(prev) ? null : prev;
        }
        catch { return null; }
    }

    /// <summary>
    /// Hostnames clients use to reach this server, from the virtual directories, Outlook Anywhere
    /// and the Autodiscover SCP. Used for SAN suggestions and the health check.
    /// </summary>
    public async Task<SortedSet<string>> GetClientHostnamesAsync(string server)
    {
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var vdirs = new[]
        {
            "Get-OwaVirtualDirectory", "Get-EcpVirtualDirectory", "Get-WebServicesVirtualDirectory",
            "Get-ActiveSyncVirtualDirectory", "Get-OabVirtualDirectory", "Get-MapiVirtualDirectory",
        };

        foreach (var cmd in vdirs)
        {
            try
            {
                var p = new Dictionary<string, object?> { ["Server"] = server, ["ADPropertiesOnly"] = null };
                foreach (var o in await _session.InvokeAsync(cmd, p, quiet: true))
                {
                    AddHost(hosts, o.Host("InternalUrl"));
                    AddHost(hosts, o.Host("ExternalUrl"));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"{cmd} on {server}: {ex.Message}");
            }
        }

        try
        {
            var p = new Dictionary<string, object?> { ["Server"] = server, ["ADPropertiesOnly"] = null };
            foreach (var o in await _session.InvokeAsync("Get-OutlookAnywhere", p, quiet: true))
            {
                AddHost(hosts, o.Host("InternalHostname"));
                AddHost(hosts, o.Host("ExternalHostname"));
            }
        }
        catch (Exception ex) { Log.Warn($"Get-OutlookAnywhere on {server}: {ex.Message}"); }

        try
        {
            var p = new Dictionary<string, object?> { ["Identity"] = server };
            foreach (var o in await _session.InvokeAsync("Get-ClientAccessService", p, quiet: true))
                AddHost(hosts, o.Host("AutoDiscoverServiceInternalUri"));
        }
        catch (Exception ex) { Log.Warn($"Get-ClientAccessService on {server}: {ex.Message}"); }

        return hosts;
    }

    public async Task<List<string>> GetAcceptedDomainsAsync()
    {
        try
        {
            var results = await _session.InvokeAsync("Get-AcceptedDomain", quiet: true);
            return results.Select(o => o.Str("DomainName")).Where(d => d.Length > 0 && !d.Contains('*')).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Get-AcceptedDomain: {ex.Message}");
            return new List<string>();
        }
    }

    private static void AddHost(ISet<string> set, string? host)
    {
        if (!string.IsNullOrWhiteSpace(host)) set.Add(host);
    }

    // ---------------------------------------------------------------- request / complete

    /// <summary>Creates a pending request on the server and returns the CSR as PEM text.</summary>
    public async Task<string> NewRequestAsync(CsrRequest r)
    {
        var subject = BuildSubject(r);
        var domains = r.Domains.Select(d => d.Trim()).Where(d => d.Length > 0)
            .Prepend(r.CommonName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var p = new Dictionary<string, object?>
        {
            ["Server"] = r.Server,
            ["GenerateRequest"] = null,
            ["SubjectName"] = subject,
            ["DomainName"] = domains,
            ["FriendlyName"] = r.FriendlyName,
            ["KeySize"] = r.KeySize,
            ["PrivateKeyExportable"] = true,
        };

        var result = await _session.InvokeAsync("New-ExchangeCertificate", p);
        var text = string.Concat(result.Select(o => o.BaseObject?.ToString()));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Exchange didn't return a certificate request.");

        Log.Success($"Request created on {r.Server} for {string.Join(", ", domains)}");
        return ToPem(text.Trim());
    }

    public static string BuildSubject(CsrRequest r)
    {
        // X.500 order as Exchange expects it: most general first.
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{key}={value.Trim().Replace(",", " ")}");
        }
        Add("C", r.Country);
        Add("S", r.County);
        Add("L", r.City);
        Add("O", r.Organisation);
        Add("OU", r.OrganisationalUnit);
        Add("CN", r.CommonName);
        return string.Join(", ", parts);
    }

    private static string ToPem(string text)
    {
        if (text.Contains("BEGIN", StringComparison.Ordinal)) return text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        var b64 = text.Replace("\r", "").Replace("\n", "");
        var sb = new StringBuilder("-----BEGIN NEW CERTIFICATE REQUEST-----").AppendLine();
        for (var i = 0; i < b64.Length; i += 64) sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
        sb.Append("-----END NEW CERTIFICATE REQUEST-----").AppendLine();
        return sb.ToString();
    }

    /// <summary>Completes a pending request with the CA's response (.cer, .crt or .p7b). Returns the new thumbprint.</summary>
    public async Task<string> CompleteRequestAsync(string server, byte[] fileData)
    {
        var p = new Dictionary<string, object?> { ["Server"] = server, ["FileData"] = fileData };
        var result = await _session.InvokeAsync("Import-ExchangeCertificate", p);
        var thumb = await ResolveImportedAsync(server, result, () => LeafOf(Load(c => c.Import(fileData))));
        Log.Success($"Request completed on {server}: {thumb}");
        return thumb;
    }

    // ---------------------------------------------------------------- assign / export / import / remove

    public async Task EnableAsync(string server, string thumbprint, IEnumerable<string> services)
    {
        var list = services.ToList();
        if (list.Count == 0) return;
        var p = new Dictionary<string, object?>
        {
            ["Server"] = server,
            ["Thumbprint"] = thumbprint,
            ["Services"] = string.Join(",", list),
            // Without -Force Exchange asks whether to replace the default SMTP certificate,
            // and there's no one to answer over remoting.
            ["Force"] = null,
            ["Confirm"] = false,
        };
        await _session.InvokeAsync("Enable-ExchangeCertificate", p);
        Log.Success($"Assigned {string.Join(", ", list)} on {server}");
    }

    public async Task<byte[]> ExportPfxAsync(string server, string thumbprint, SecureString password)
    {
        var p = new Dictionary<string, object?>
        {
            ["Server"] = server,
            ["Thumbprint"] = thumbprint,
            ["BinaryEncoded"] = null,
            ["Password"] = password,
        };
        var result = (await _session.InvokeAsync("Export-ExchangeCertificate", p)).FirstOrDefault()
                     ?? throw new InvalidOperationException("Export returned nothing.");
        return result.Bytes("FileData") ?? (result.BaseObject as byte[])
               ?? throw new InvalidOperationException("Export didn't return file data. Is the private key exportable?");
    }

    public async Task<string> ImportPfxAsync(string server, byte[] pfx, SecureString password, string? friendlyName = null)
    {
        var p = new Dictionary<string, object?>
        {
            ["Server"] = server,
            ["FileData"] = pfx,
            ["Password"] = password,
            ["PrivateKeyExportable"] = true,
        };
        if (!string.IsNullOrWhiteSpace(friendlyName)) p["FriendlyName"] = friendlyName;
        var result = await _session.InvokeAsync("Import-ExchangeCertificate", p);
        var plain = new System.Net.NetworkCredential(string.Empty, password).Password;
        var thumb = await ResolveImportedAsync(server, result, () =>
        {
            var certs = Load(c => c.Import(pfx, plain, X509KeyStorageFlags.EphemeralKeySet));
            return certs.FirstOrDefault(c => c.HasPrivateKey) ?? LeafOf(certs);
        });
        Log.Success($"Imported {thumb} on {server}");
        return thumb;
    }

    /// <summary>
    /// The thumbprint of what was just imported. Import-ExchangeCertificate doesn't always return the certificate
    /// over remote PowerShell (it can come back empty when the file includes the chain), so if it doesn't, the
    /// thumbprint is read from the file itself and checked with Get-ExchangeCertificate on the server.
    /// </summary>
    private async Task<string> ResolveImportedAsync(string server, IReadOnlyCollection<System.Management.Automation.PSObject> result, Func<X509Certificate2?> fromFile)
    {
        var returned = result.Select(o => o.Str("Thumbprint")).FirstOrDefault(t => t.Length == 40);
        if (returned is not null) return returned;

        var leaf = fromFile() ?? throw new InvalidOperationException("Exchange didn't say what it imported, and the certificate couldn't be read from the file.");
        var thumb = leaf.Thumbprint;
        var check = await _session.InvokeAsync("Get-ExchangeCertificate",
            new Dictionary<string, object?> { ["Server"] = server, ["Thumbprint"] = thumb }, quiet: true);
        if (!check.Any())
            throw new InvalidOperationException($"Exchange reported no error, but {thumb} ({leaf.Subject}) isn't on {server}.");
        Log.Info($"Exchange didn't return the imported certificate; found it on {server} by its thumbprint");
        return thumb;
    }

    private static X509Certificate2Collection Load(Action<X509Certificate2Collection> import)
    {
        var c = new X509Certificate2Collection();
        try { import(c); } catch { }
        return c;
    }

    /// <summary>The end-entity certificate: the one that isn't a CA.</summary>
    private static X509Certificate2? LeafOf(X509Certificate2Collection certs) =>
        certs.FirstOrDefault(c => c.Extensions.OfType<X509BasicConstraintsExtension>().All(b => !b.CertificateAuthority))
        ?? certs.FirstOrDefault();

    public async Task RemoveAsync(string server, string thumbprint)
    {
        var p = new Dictionary<string, object?> { ["Server"] = server, ["Thumbprint"] = thumbprint, ["Confirm"] = false };
        await _session.InvokeAsync("Remove-ExchangeCertificate", p);
        Log.Success($"Removed {thumbprint} from {server}");
    }

    /// <summary>
    /// Copies a certificate from one server to the others and assigns the same services everywhere.
    /// Servers that already hold the thumbprint are not re-imported.
    /// </summary>
    public async Task DeployAsync(string sourceServer, string thumbprint, IReadOnlyCollection<string> targets,
        IReadOnlyCollection<string> services, IProgress<string>? progress = null)
    {
        var password = RandomPassword();
        progress?.Report($"Exporting from {sourceServer}");
        var pfx = await ExportPfxAsync(sourceServer, thumbprint, password);

        foreach (var target in targets)
        {
            var existing = await GetCertificatesAsync(target);
            if (!existing.Any(c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                progress?.Report($"Importing on {target}");
                await ImportPfxAsync(target, pfx, password);
            }
            else
            {
                Log.Info($"{target} already has {thumbprint}");
            }

            if (services.Count > 0)
            {
                progress?.Report($"Assigning services on {target}");
                await EnableAsync(target, thumbprint, services);
            }
        }

        if (services.Count > 0 && !targets.Contains(sourceServer, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report($"Assigning services on {sourceServer}");
            await EnableAsync(sourceServer, thumbprint, services);
        }
    }

    public static SecureString RandomPassword()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var s = new SecureString();
        foreach (var c in Convert.ToBase64String(bytes)) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }

    public static SecureString ToSecure(string value)
    {
        var s = new SecureString();
        foreach (var c in value) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }
}
