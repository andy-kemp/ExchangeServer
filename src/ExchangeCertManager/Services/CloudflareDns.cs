using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExchangeCertManager.Services;

/// <summary>
/// Let's Encrypt DNS-01 through the Cloudflare API: adds the _acme-challenge TXT records, waits until they resolve,
/// and removes them again. Needs an API token with Zone > DNS > Edit on the zones concerned.
/// The token is stored like the ACME account key: DPAPI (LocalMachine) in ProgramData\ExchangeCertManager, which
/// only Administrators and SYSTEM can read, so the scheduled renewal can use it. It's never logged.
/// </summary>
public sealed class CloudflareDns
{
    private const string Api = "https://api.cloudflare.com/client/v4";
    private static readonly string TokenFile = Path.Combine(AppPaths.ProgramData, "cloudflare.token");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _token;
    private readonly Dictionary<string, (string Id, string Name)> _zones = new(StringComparer.OrdinalIgnoreCase);

    public CloudflareDns(string token) => _token = token;

    /// <summary>The saved token, or throws if there isn't one.</summary>
    public static CloudflareDns FromSavedToken() =>
        new(LoadToken() ?? throw new InvalidOperationException("No Cloudflare API token is saved. Add one on the Let's Encrypt page (3 Validation)."));

    // ---------------------------------------------------------------- token storage

    public static bool HasToken => File.Exists(TokenFile);

    public static void SaveToken(string token)
    {
        AcmeService.EnsureSecureDirectory(AppPaths.ProgramData);
        File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.LocalMachine));
        Log.Success("Cloudflare API token saved (encrypted, Administrators and SYSTEM only)");
    }

    public static string? LoadToken()
    {
        if (!File.Exists(TokenFile)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.LocalMachine));
    }

    public static void DeleteToken()
    {
        if (File.Exists(TokenFile)) File.Delete(TokenFile);
        Log.Info("Cloudflare API token removed");
    }

    // ---------------------------------------------------------------- API

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.UserAgent.ParseAdd($"ExchangeCertManager/{UpdateService.CurrentText}");
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch { throw new InvalidOperationException($"Cloudflare returned {(int)resp.StatusCode} {resp.ReasonPhrase}."); }
        if (!resp.IsSuccessStatusCode || !root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
        {
            var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                ? string.Join("; ", e.EnumerateArray().Select(x => x.TryGetProperty("message", out var m) ? m.GetString() : x.ToString()))
                : "";
            throw new InvalidOperationException($"Cloudflare: {(errors.Length > 0 ? errors : $"{(int)resp.StatusCode} {resp.ReasonPhrase}")}");
        }
        return root;
    }

    /// <summary>Checks the token is valid and active, and lists the zones it can see.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(CancellationToken ct = default)
    {
        var verify = await SendAsync(HttpMethod.Get, "/user/tokens/verify", ct: ct);
        var status = verify.GetProperty("result").GetProperty("status").GetString();
        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The Cloudflare token is {status}.");
        var zones = await SendAsync(HttpMethod.Get, "/zones?per_page=50", ct: ct);
        return zones.GetProperty("result").EnumerateArray().Select(z => z.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
    }

    /// <summary>The Cloudflare zone a name belongs to: the longest zone name it ends with that the token can see.</summary>
    public async Task<(string Id, string Name)> FindZoneAsync(string host, CancellationToken ct = default)
    {
        var labels = host.TrimStart('*', '.').TrimEnd('.').Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var candidate = string.Join('.', labels.Skip(i));
            if (_zones.TryGetValue(candidate, out var cached)) return cached;
            var found = await SendAsync(HttpMethod.Get, $"/zones?name={Uri.EscapeDataString(candidate)}", ct: ct);
            var zone = found.GetProperty("result").EnumerateArray().FirstOrDefault();
            if (zone.ValueKind == JsonValueKind.Object)
            {
                var result = (zone.GetProperty("id").GetString()!, zone.GetProperty("name").GetString()!);
                _zones[candidate] = result;
                return result;
            }
        }
        throw new InvalidOperationException($"No Cloudflare zone for {host} that this token can edit. Give the token Zone > DNS > Edit on that zone.");
    }

    /// <summary>Adds a TXT record and returns (zone id, record id) for removal.</summary>
    public async Task<(string ZoneId, string RecordId)> AddTxtAsync(string name, string value, CancellationToken ct = default)
    {
        var zone = await FindZoneAsync(name, ct);
        var created = await SendAsync(HttpMethod.Post, $"/zones/{zone.Id}/dns_records",
            new { type = "TXT", name, content = $"\"{value}\"", ttl = 60, comment = "Exchange Certificate Manager: Let's Encrypt challenge, removed automatically" }, ct);
        var id = created.GetProperty("result").GetProperty("id").GetString()!;
        Log.Info($"Cloudflare: added TXT {name} in zone {zone.Name}");
        return (zone.Id, id);
    }

    public async Task RemoveAsync(string zoneId, string recordId, string name)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{recordId}");
            Log.Info($"Cloudflare: removed TXT {name}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Cloudflare: couldn't remove TXT {name} ({ex.Message}). Delete it in the Cloudflare dashboard.");
        }
    }

    /// <summary>
    /// Waits until every record resolves publicly (DNS over HTTPS to 1.1.1.1, which answers for Cloudflare zones
    /// straight away). Gives up waiting after two minutes and lets Let's Encrypt try anyway.
    /// </summary>
    public static async Task WaitForRecordsAsync(IReadOnlyList<(string Name, string Value)> records, IProgress<string>? progress, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        var remaining = records.ToList();
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            progress?.Report($"Waiting for {remaining.Count} DNS record(s) to appear");
            var still = new List<(string Name, string Value)>();
            foreach (var r in remaining)
            {
                if (!await ResolvesAsync(r.Name, r.Value, ct)) still.Add(r);
            }
            remaining = still;
            if (remaining.Count > 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        if (remaining.Count > 0) Log.Warn($"Couldn't see {string.Join(", ", remaining.Select(r => r.Name))} yet; asking Let's Encrypt to check anyway");
        else Log.Success("DNS records are visible");
    }

    private static async Task<bool> ResolvesAsync(string name, string value, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("Answer", out var answers)
                   && answers.EnumerateArray().Any(a => a.TryGetProperty("data", out var d) && (d.GetString() ?? "").Trim('"') == value);
        }
        catch
        {
            return false;
        }
    }
}
