using System.Management.Automation;
using ExchangeCertManager.Helpers;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

/// <summary>
/// Reads and sets the client access URLs: the virtual directories, Outlook Anywhere and the Autodiscover SCP.
/// The PowerShell virtual directory is deliberately left alone: the app connects through it.
/// </summary>
public sealed class VirtualDirectoryService
{
    private readonly ExchangeSession _session;

    public VirtualDirectoryService(ExchangeSession session) => _session = session;

    /// <summary>The URL-based virtual directories, with the path each URL ends in.</summary>
    public static readonly VdirKind[] UrlKinds =
    {
        new("OWA", "Outlook on the web", "Get-OwaVirtualDirectory", "Set-OwaVirtualDirectory", "/owa"),
        new("ECP", "Exchange admin center", "Get-EcpVirtualDirectory", "Set-EcpVirtualDirectory", "/ecp"),
        new("EWS", "Exchange Web Services", "Get-WebServicesVirtualDirectory", "Set-WebServicesVirtualDirectory", "/EWS/Exchange.asmx"),
        new("ActiveSync", "ActiveSync", "Get-ActiveSyncVirtualDirectory", "Set-ActiveSyncVirtualDirectory", "/Microsoft-Server-ActiveSync"),
        new("OAB", "Offline address book", "Get-OabVirtualDirectory", "Set-OabVirtualDirectory", "/OAB"),
        new("MAPI", "MAPI over HTTP", "Get-MapiVirtualDirectory", "Set-MapiVirtualDirectory", "/mapi"),
    };

    public const string OutlookAnywhere = "Outlook Anywhere";
    public const string Autodiscover = "Autodiscover";

    public async Task<List<VdirEntry>> GetAsync(IEnumerable<ExchangeServerInfo> servers)
    {
        var list = new List<VdirEntry>();
        foreach (var server in servers)
        {
            foreach (var k in UrlKinds)
            {
                try
                {
                    var p = new Dictionary<string, object?> { ["Server"] = server.Name, ["ADPropertiesOnly"] = null };
                    foreach (var o in await _session.InvokeAsync(k.GetCommand, p, quiet: true))
                        list.Add(new VdirEntry
                        {
                            Server = server.Name, Kind = k.Key, Label = k.Label, Identity = o.Str("Identity"),
                            Internal = o.Str("InternalUrl"), External = o.Str("ExternalUrl"),
                        });
                }
                catch (Exception ex) { Log.Warn($"{k.GetCommand} on {server.Name}: {ex.Message}"); }
            }

            try
            {
                var p = new Dictionary<string, object?> { ["Server"] = server.Name, ["ADPropertiesOnly"] = null };
                foreach (var o in await _session.InvokeAsync("Get-OutlookAnywhere", p, quiet: true))
                    list.Add(new VdirEntry
                    {
                        Server = server.Name, Kind = OutlookAnywhere, Label = "Outlook Anywhere (hostnames)", Identity = o.Str("Identity"),
                        Internal = o.Str("InternalHostname"), External = o.Str("ExternalHostname"),
                        InternalAuth = o.Str("InternalClientAuthenticationMethod"),
                        ExternalAuth = o.Str("ExternalClientAuthenticationMethod"),
                    });
            }
            catch (Exception ex) { Log.Warn($"Get-OutlookAnywhere on {server.Name}: {ex.Message}"); }

            try
            {
                var p = new Dictionary<string, object?> { ["Identity"] = server.Name };
                foreach (var o in await _session.InvokeAsync("Get-ClientAccessService", p, quiet: true))
                    list.Add(new VdirEntry
                    {
                        Server = server.Name, Kind = Autodiscover, Label = "Autodiscover (internal SCP)", Identity = server.Name,
                        Internal = o.Str("AutoDiscoverServiceInternalUri"), External = "",
                    });
            }
            catch (Exception ex) { Log.Warn($"Get-ClientAccessService on {server.Name}: {ex.Message}"); }
        }
        return list;
    }

    /// <summary>The current Outlook Anywhere authentication method, or Exchange's default when there isn't one.</summary>
    static string AuthOrDefault(string current, string fallback) =>
        string.IsNullOrWhiteSpace(current) || current.Equals("Misconfigured", StringComparison.OrdinalIgnoreCase) ? fallback : current;

    /// <summary>Works out the commands needed to move every ticked entry to the namespace. Unchanged entries are skipped.</summary>
    public static List<VdirChange> Plan(IEnumerable<VdirEntry> current, NamespacePlan plan)
    {
        var changes = new List<VdirChange>();
        foreach (var e in current.Where(e => plan.Servers.Contains(e.Server, StringComparer.OrdinalIgnoreCase) && plan.Kinds.Contains(e.Kind)))
        {
            var p = new Dictionary<string, object?> { ["Identity"] = e.Identity };
            var before = new List<string>();
            var after = new List<string>();

            if (e.Kind == Autodiscover)
            {
                if (!plan.SetInternal || string.IsNullOrWhiteSpace(plan.AutodiscoverHost)) continue;
                var target = $"https://{plan.AutodiscoverHost}/Autodiscover/Autodiscover.xml";
                if (SameUrl(e.Internal, target)) continue;
                p["AutoDiscoverServiceInternalUri"] = target;
                changes.Add(new VdirChange(e, "Set-ClientAccessService", p, e.Internal, target));
                continue;
            }

            if (e.Kind == OutlookAnywhere)
            {
                if (plan.SetInternal && !SameHost(e.Internal, plan.InternalHost))
                {
                    p["InternalHostname"] = plan.InternalHost;
                    p["InternalClientsRequireSsl"] = true;
                    // Exchange insists on an authentication method when a hostname is first set; keep the current one
                    p["InternalClientAuthenticationMethod"] = AuthOrDefault(e.InternalAuth, "Ntlm");
                    before.Add($"internal {Show(e.Internal)}"); after.Add($"internal {plan.InternalHost}");
                }
                if (plan.SetExternal && !SameHost(e.External, plan.ExternalHost))
                {
                    p["ExternalHostname"] = plan.ExternalHost;
                    p["ExternalClientsRequireSsl"] = true;
                    // Without this, setting an external hostname for the first time fails with "you must also specify
                    // the DefaultAuthenticationMethod or ExternalClientAuthenticationMethod parameter"
                    p["ExternalClientAuthenticationMethod"] = AuthOrDefault(e.ExternalAuth, "Negotiate");
                    before.Add($"external {Show(e.External)}"); after.Add($"external {plan.ExternalHost}");
                }
                if (p.Count > 1) changes.Add(new VdirChange(e, "Set-OutlookAnywhere", p, string.Join("; ", before), string.Join("; ", after)));
                continue;
            }

            var kind = UrlKinds.First(k => k.Key == e.Kind);
            if (plan.SetInternal)
            {
                var target = $"https://{plan.InternalHost}{kind.Path}";
                if (!SameUrl(e.Internal, target))
                {
                    p["InternalUrl"] = target;
                    before.Add($"internal {Show(e.Internal)}"); after.Add($"internal {target}");
                }
            }
            if (plan.SetExternal)
            {
                if (e.Kind == "ECP" && plan.EcpExternalBlank)
                {
                    if (!string.IsNullOrWhiteSpace(e.External))
                    {
                        p["ExternalUrl"] = ExchangeSession.Null;
                        before.Add($"external {e.External}"); after.Add("external (none)");
                    }
                }
                else
                {
                    var target = $"https://{plan.ExternalHost}{kind.Path}";
                    if (!SameUrl(e.External, target))
                    {
                        p["ExternalUrl"] = target;
                        before.Add($"external {Show(e.External)}"); after.Add($"external {target}");
                    }
                }
            }
            if (p.Count > 1) changes.Add(new VdirChange(e, kind.SetCommand, p, string.Join("; ", before), string.Join("; ", after)));
        }
        return changes;
    }

    /// <summary>Applies the changes one at a time. Carries on past a failure and returns what failed.</summary>
    public async Task<List<(VdirChange Change, string Error)>> ApplyAsync(IReadOnlyList<VdirChange> changes, IProgress<string>? progress = null)
    {
        var failed = new List<(VdirChange, string)>();
        foreach (var c in changes)
        {
            progress?.Report($"{c.Entry.Server}: {c.Entry.Label}");
            try { await _session.InvokeAsync(c.Command, c.Parameters); }
            catch (Exception ex)
            {
                failed.Add((c, ex.Message));
                Log.Error($"{c.Entry.Server} {c.Entry.Label}: {ex.Message}");
            }
        }
        Log.Success($"{changes.Count - failed.Count} of {changes.Count} change(s) applied");
        return failed;
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static bool SameUrl(string current, string target) =>
        string.Equals(current.Trim().TrimEnd('/'), target.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameHost(string current, string host) =>
        string.Equals(current.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Hostname of a URL or bare hostname, lower case, or empty.</summary>
    public static string HostOf(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Uri.TryCreate(value, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : value.Trim().ToLowerInvariant();
    }
}
