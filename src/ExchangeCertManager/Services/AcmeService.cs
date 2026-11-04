using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

public sealed record AcmeIssueRequest(
    IReadOnlyList<string> Domains,
    string Email,
    bool Staging,
    AcmeChallengeType Challenge,
    // Server name -> IIS web root (local path or UNC). HTTP-01 files go to all of them.
    IReadOnlyDictionary<string, string> Webroots);

/// <summary>
/// Let's Encrypt issuance with Certes. HTTP-01 drops the challenge file into the Default Web Site
/// on every selected server (so it works behind a load balancer). DNS-01 is either manual (the UI shows
/// the TXT records and waits for you to add them) or through the Cloudflare API, which adds and removes
/// them itself and so can renew unattended. Wildcards need DNS-01.
/// </summary>
public sealed class AcmeService
{
    private static readonly string AcmeDir = Path.Combine(AppPaths.ProgramData, "acme");

    /// <summary>Called for DNS-01 with (record name, TXT value). Return when the records are in DNS.</summary>
    public Func<IReadOnlyList<(string Name, string Value)>, Task>? ConfirmDnsRecords { get; set; }

    /// <summary>Used for <see cref="AcmeChallengeType.Dns01Cloudflare"/>; the saved token if not set.</summary>
    public CloudflareDns? Cloudflare { get; set; }

    public async Task<(byte[] Pfx, string Password, DateTime NotAfter)> IssueAsync(AcmeIssueRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (req.Domains.Count == 0) throw new ArgumentException("Add at least one domain name.");
        if (req.Challenge == AcmeChallengeType.Http01 && req.Domains.Any(d => d.StartsWith("*.", StringComparison.Ordinal)))
            throw new InvalidOperationException("Wildcard names need the DNS challenge.");

        var server = req.Staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
        progress?.Report("Opening Let's Encrypt account");
        var acme = await GetContextAsync(server, req.Email);

        progress?.Report("Placing order");
        Log.Info($"ACME order for {string.Join(", ", req.Domains)}{(req.Staging ? " (staging)" : "")}");
        var order = await acme.NewOrder(req.Domains.ToList());
        var authorizations = (await order.Authorizations()).ToList();

        var written = new List<string>();
        try
        {
            if (req.Challenge == AcmeChallengeType.Http01)
            {
                foreach (var authz in authorizations)
                {
                    var resource = await authz.Resource();
                    if (resource.Status == AuthorizationStatus.Valid) continue;
                    var challenge = await authz.Http();
                    written.AddRange(WriteHttpChallenge(req.Webroots.Values, challenge.Token, challenge.KeyAuthz));
                    progress?.Report($"Validating {resource.Identifier.Value}");
                    await challenge.Validate();
                    await WaitForValidAsync(authz, resource.Identifier.Value, ct);
                }
            }
            else
            {
                var pending = new List<(IAuthorizationContext Authz, IChallengeContext Challenge, string Host)>();
                var records = new List<(string Name, string Value)>();
                foreach (var authz in authorizations)
                {
                    var resource = await authz.Resource();
                    if (resource.Status == AuthorizationStatus.Valid) continue;
                    var challenge = await authz.Dns();
                    var host = resource.Identifier.Value;
                    records.Add(($"_acme-challenge.{host}", acme.AccountKey.DnsTxt(challenge.Token)));
                    pending.Add((authz, challenge, host));
                }

                var added = new List<(string ZoneId, string RecordId, string Name)>();
                try
                {
                    if (records.Count > 0 && req.Challenge == AcmeChallengeType.Dns01Cloudflare)
                    {
                        var cf = Cloudflare ?? CloudflareDns.FromSavedToken();
                        foreach (var (name, value) in records)
                        {
                            progress?.Report($"Adding {name} in Cloudflare");
                            var (zoneId, recordId) = await cf.AddTxtAsync(name, value, ct);
                            added.Add((zoneId, recordId, name));
                        }
                        await CloudflareDns.WaitForRecordsAsync(records, progress, ct);
                    }
                    else if (records.Count > 0)
                    {
                        if (ConfirmDnsRecords is null) throw new InvalidOperationException("The DNS challenge needs someone to add the records.");
                        progress?.Report("Waiting for DNS records");
                        await ConfirmDnsRecords(records);
                    }

                    foreach (var (authz, challenge, host) in pending)
                    {
                        progress?.Report($"Validating {host}");
                        await challenge.Validate();
                        await WaitForValidAsync(authz, host, ct);
                    }
                }
                finally
                {
                    // Always tidy up the challenge records, whatever happened
                    if (added.Count > 0)
                    {
                        var cf = Cloudflare ?? CloudflareDns.FromSavedToken();
                        foreach (var (zoneId, recordId, name) in added) await cf.RemoveAsync(zoneId, recordId, name);
                    }
                }
            }
        }
        finally
        {
            foreach (var f in written)
            {
                try { File.Delete(f); } catch { }
            }
        }

        progress?.Report("Issuing certificate");
        var key = KeyFactory.NewKey(KeyAlgorithm.RS256); // Exchange needs RSA
        var commonName = req.Domains.FirstOrDefault(d => !d.StartsWith("*.", StringComparison.Ordinal)) ?? req.Domains[0];
        var chain = await order.Generate(new CsrInfo { CommonName = commonName }, key);

        // Build the PFX with .NET so the intermediates are included without relying on Certes' root store.
        using var leaf = X509Certificate2.CreateFromPem(chain.Certificate.ToPem(), key.ToPem());
        var collection = new X509Certificate2Collection(leaf);
        foreach (var issuer in chain.Issuers)
            collection.Add(X509Certificate2.CreateFromPem(issuer.ToPem()));

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = collection.Export(X509ContentType.Pkcs12, password)
                  ?? throw new InvalidOperationException("Couldn't build the PFX.");

        Log.Success($"Let's Encrypt issued {commonName}, valid to {leaf.NotAfter:d}");
        return (pfx, password, leaf.NotAfter);
    }

    private static async Task WaitForValidAsync(IAuthorizationContext authz, string host, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await authz.Resource();
            if (r.Status == AuthorizationStatus.Valid)
            {
                Log.Success($"Validated {host}");
                return;
            }
            if (r.Status == AuthorizationStatus.Invalid)
            {
                var detail = r.Challenges?.Select(c => c.Error?.Detail).FirstOrDefault(d => !string.IsNullOrEmpty(d));
                throw new InvalidOperationException($"Let's Encrypt couldn't validate {host}: {detail ?? "no detail given"}");
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"Validation of {host} didn't finish in time.");
    }

    /// <summary>
    /// Writes the token to {webroot}\.well-known\acme-challenge, with a web.config that turns off the
    /// HTTP→HTTPS / OWA redirect many Exchange builds put on the Default Web Site and serves the
    /// extensionless file as text.
    /// </summary>
    private static IEnumerable<string> WriteHttpChallenge(IEnumerable<string> webroots, string token, string keyAuthz)
    {
        var files = new List<string>();
        foreach (var root in webroots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(root, ".well-known", "acme-challenge");
            System.IO.Directory.CreateDirectory(dir);
            var config = Path.Combine(dir, "web.config");
            if (!File.Exists(config)) File.WriteAllText(config, ChallengeWebConfig);
            var file = Path.Combine(dir, token);
            File.WriteAllText(file, keyAuthz, new UTF8Encoding(false));
            files.Add(file);
            Log.Info($"Challenge file written to {dir}");
        }
        return files;
    }

    private const string ChallengeWebConfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
          <system.webServer>
            <httpRedirect enabled="false" />
            <staticContent>
              <remove fileExtension="." />
              <mimeMap fileExtension="." mimeType="text/plain" />
            </staticContent>
            <handlers>
              <clear />
              <add name="StaticFile" path="*" verb="*" modules="StaticFileModule" resourceType="Either" requireAccess="Read" />
            </handlers>
            <security>
              <access sslFlags="None" />
            </security>
          </system.webServer>
        </configuration>
        """;

    // ---------------------------------------------------------------- account key

    private static async Task<AcmeContext> GetContextAsync(Uri server, string email)
    {
        var keyFile = Path.Combine(AcmeDir, server.Host.Contains("staging") ? "account-staging.key" : "account.key");
        EnsureSecureDirectory(AcmeDir);

        if (File.Exists(keyFile))
        {
            var pem = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, DataProtectionScope.LocalMachine));
            var existing = new AcmeContext(server, KeyFactory.FromPem(pem));
            await existing.Account(); // confirms the account still exists
            return existing;
        }

        var acme = new AcmeContext(server);
        await acme.NewAccount(email, true);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(acme.AccountKey.ToPem()), null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(keyFile, bytes);
        Log.Success($"Created Let's Encrypt account for {email}");
        return acme;
    }

    /// <summary>ProgramData\ExchangeCertManager readable only by Administrators and SYSTEM.</summary>
    public static void EnsureSecureDirectory(string path) => AppPaths.EnsureSecureDirectory(path);

    // ---------------------------------------------------------------- saved renewals

    private static readonly string RenewalsFile = Path.Combine(AppPaths.ProgramData, "renewals.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<AcmeRenewal> LoadRenewals()
    {
        try
        {
            return File.Exists(RenewalsFile)
                ? JsonSerializer.Deserialize<List<AcmeRenewal>>(File.ReadAllText(RenewalsFile)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read {RenewalsFile}: {ex.Message}");
            return new();
        }
    }

    public static void SaveRenewals(List<AcmeRenewal> renewals)
    {
        EnsureSecureDirectory(AppPaths.ProgramData);
        File.WriteAllText(RenewalsFile, JsonSerializer.Serialize(renewals, JsonOptions));
    }

    public static void SaveRenewal(AcmeRenewal renewal)
    {
        var all = LoadRenewals();
        var existing = all.FindIndex(r => r.Id == renewal.Id
            || r.Domains.OrderBy(d => d).SequenceEqual(renewal.Domains.OrderBy(d => d), StringComparer.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            renewal.Id = all[existing].Id;
            all[existing] = renewal;
        }
        else all.Add(renewal);
        SaveRenewals(all);
    }

    /// <summary>Default IIS web root for a server: local path if it's this machine, otherwise the admin share.</summary>
    public static string DefaultWebroot(ExchangeServerInfo server)
    {
        var local = Environment.MachineName.Equals(server.Name, StringComparison.OrdinalIgnoreCase);
        return local ? @"C:\inetpub\wwwroot" : $@"\\{server.Fqdn}\c$\inetpub\wwwroot";
    }
}
