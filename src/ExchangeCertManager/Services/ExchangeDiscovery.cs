using System.DirectoryServices;
using System.Text.RegularExpressions;

namespace ExchangeCertManager.Services;

/// <summary>An Exchange server found in Active Directory.</summary>
public sealed record DiscoveredServer(string Name, string Fqdn, string Version, string Site, bool InThisSite)
{
    public string Display => $"{Fqdn}   ·   {Version}{(Site.Length > 0 ? $"   ·   {Site}" : "")}";
}

/// <summary>
/// Finds the organisation's Exchange servers in Active Directory (the Exchange objects in the configuration
/// partition), so nobody has to type an FQDN. Read-only LDAP as the signed-in user, which any domain user can do.
/// Edge Transport servers are left out (they can't be managed over the Exchange PowerShell endpoint).
/// </summary>
public static class ExchangeDiscovery
{
    private const int EdgeRole = 64;

    public static Task<List<DiscoveredServer>> FindAsync() => Task.Run(Find);

    private static List<DiscoveredServer> Find()
    {
        using var rootDse = new DirectoryEntry("LDAP://RootDSE");
        var config = (string)rootDse.Properties["configurationNamingContext"].Value!;
        using var root = new DirectoryEntry($"LDAP://CN=Microsoft Exchange,CN=Services,{config}");
        using var searcher = new DirectorySearcher(root, "(objectClass=msExchExchangeServer)",
            new[] { "name", "networkAddress", "serialNumber", "msExchCurrentServerRoles", "msExchServerSite" })
        {
            PageSize = 200,
            SearchScope = SearchScope.Subtree,
        };

        var mySite = "";
        try { mySite = System.DirectoryServices.ActiveDirectory.ActiveDirectorySite.GetComputerSite().Name; } catch { }

        var found = new List<DiscoveredServer>();
        using var results = searcher.FindAll();
        foreach (SearchResult r in results)
        {
            var name = First(r, "name");
            if (name.Length == 0) continue;
            if (int.TryParse(First(r, "msExchCurrentServerRoles"), out var roles) && (roles & EdgeRole) != 0) continue;

            // networkAddress holds entries like "ncacn_ip_tcp:ex01.ad.contoso.com"
            var fqdn = r.Properties["networkAddress"].Cast<object>().Select(o => o.ToString() ?? "")
                .Where(a => a.StartsWith("ncacn_ip_tcp:", StringComparison.OrdinalIgnoreCase))
                .Select(a => a[13..]).FirstOrDefault() ?? name;
            var site = Regex.Match(First(r, "msExchServerSite"), "^CN=([^,]+)").Groups[1].Value;
            found.Add(new DiscoveredServer(name, fqdn.ToLowerInvariant(), VersionName(First(r, "serialNumber")), site,
                mySite.Length > 0 && site.Equals(mySite, StringComparison.OrdinalIgnoreCase)));
        }

        // This AD site first, then newest first, then by name
        return found.OrderByDescending(s => s.InThisSite).ThenByDescending(s => s.Version).ThenBy(s => s.Name).ToList();
    }

    private static string First(SearchResult r, string attribute) =>
        r.Properties[attribute] is { Count: > 0 } v ? v[0]?.ToString() ?? "" : "";

    /// <summary>"Version 15.2 (Build 1748.10)" → "Exchange 2019 / SE (15.2.1748)".</summary>
    private static string VersionName(string serial)
    {
        var m = Regex.Match(serial, @"Version (\d+)\.(\d+) \(Build (\d+)");
        if (!m.Success) return serial;
        var (major, minor, build) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        var name = (major, minor) switch
        {
            (15, 2) when build >= 2562 => "Exchange SE",
            (15, 2) => "Exchange 2019",
            (15, 1) => "Exchange 2016",
            (15, 0) => "Exchange 2013",
            _ => "Exchange",
        };
        return $"{name} ({major}.{minor}.{build})";
    }
}
