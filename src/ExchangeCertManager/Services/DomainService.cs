using System.Collections;
using System.Management.Automation;
using ExchangeCertManager.Helpers;

namespace ExchangeCertManager.Services;

public sealed class AcceptedDomainInfo
{
    public string Name { get; init; } = "";
    public string DomainName { get; init; } = "";
    public string DomainType { get; init; } = "";
    public bool Default { get; init; }
    /// <summary>The Microsoft 365 routing and tenant domains: needed for hybrid, never changed here.</summary>
    public bool IsMicrosoft => DomainService.IsMicrosoftDomain(DomainName);
    public string DefaultText => Default ? "Default" : "";
    public string Note => IsMicrosoft ? "Microsoft 365 (hybrid): left alone" : "";
}

public sealed class AddressPolicyInfo
{
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";
    public List<string> Templates { get; init; } = new();
    public string RecipientFilter { get; init; } = "";
    public bool IsDefault => Priority.Equals("Lowest", StringComparison.OrdinalIgnoreCase);
    public string Primary => Templates.FirstOrDefault(t => t.StartsWith("SMTP:", StringComparison.Ordinal))?[5..] ?? "";
    public string Others => string.Join(", ", Templates.Where(t => !t.StartsWith("SMTP:", StringComparison.Ordinal)).Select(t => t.Contains(':') ? t[(t.IndexOf(':') + 1)..] : t));
    public string AppliesTo => IsDefault ? "Everyone (default policy)" : RecipientFilter;
}

/// <summary>Who still uses a domain, before it's retired.</summary>
public sealed class DomainUsage
{
    public string Domain { get; init; } = "";
    public List<(string Name, string Type, string Address)> Primary { get; } = new();
    public List<(string Name, string Identity, string Type, string Address)> Secondary { get; } = new();
    public List<string> Policies { get; } = new();
    public List<string> Connectors { get; } = new();
    public bool CanRemove => Primary.Count == 0 && Policies.Count == 0;
}

/// <summary>
/// Accepted domains and email address policies: add a domain, make it the default, move the default policy to it,
/// and retire an old one (typically the AD domain) in the order Exchange needs: the policy first, then any leftover
/// addresses, then the domain. The Microsoft 365 domains (onmicrosoft.com) are never touched.
/// </summary>
public sealed class DomainService
{
    private readonly ExchangeSession _session;
    public DomainService(ExchangeSession session) => _session = session;

    public static bool IsMicrosoftDomain(string domain) =>
        domain.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".onmicrosoft.us", StringComparison.OrdinalIgnoreCase);

    private static string DomainOf(string template)
    {
        var at = template.LastIndexOf('@');
        return at < 0 ? "" : template[(at + 1)..].Trim().ToLowerInvariant();
    }

    public async Task<List<AcceptedDomainInfo>> GetDomainsAsync()
    {
        var results = await _session.InvokeAsync("Get-AcceptedDomain", select: new[] { "Name", "DomainName", "DomainType", "Default" });
        return results.Select(o => new AcceptedDomainInfo
        {
            Name = o.Str("Name"),
            DomainName = o.Str("DomainName").ToLowerInvariant(),
            DomainType = o.Str("DomainType"),
            Default = o.Bool("Default"),
        }).OrderByDescending(d => d.Default).ThenBy(d => d.IsMicrosoft).ThenBy(d => d.DomainName).ToList();
    }

    public async Task<List<AddressPolicyInfo>> GetPoliciesAsync()
    {
        var results = await _session.InvokeAsync("Get-EmailAddressPolicy", select: new[] { "Name", "Priority", "EnabledEmailAddressTemplates", "RecipientFilter" });
        return results.Select(o => new AddressPolicyInfo
        {
            Name = o.Str("Name"),
            Priority = o.Str("Priority"),
            Templates = o.List("EnabledEmailAddressTemplates"),
            RecipientFilter = o.Str("RecipientFilter"),
        }).OrderBy(p => p.IsDefault).ThenBy(p => int.TryParse(p.Priority, out var n) ? n : 0).ToList();
    }

    public Task AddDomainAsync(string domain, string type) =>
        _session.InvokeAsync("New-AcceptedDomain", new Dictionary<string, object?>
        {
            ["Name"] = domain, ["DomainName"] = domain, ["DomainType"] = type,
        });

    public Task MakeDefaultAsync(string name) =>
        _session.InvokeAsync("Set-AcceptedDomain", new Dictionary<string, object?> { ["Identity"] = name, ["MakeDefault"] = true });

    /// <summary>
    /// The default policy's new template list: the new domain as primary, the old primary kept as a secondary
    /// unless it's being dropped, other secondaries kept, and the onmicrosoft.com ones always kept.
    /// </summary>
    public static List<string> NewTemplates(AddressPolicyInfo policy, string newDomain, bool keepOldPrimary, string? dropDomain)
    {
        var local = policy.Primary.Contains('@') ? policy.Primary[..policy.Primary.IndexOf('@')] : "%m";
        var result = new List<string> { $"SMTP:{local}@{newDomain}" };
        foreach (var t in policy.Templates)
        {
            var d = DomainOf(t);
            if (d == newDomain) continue;
            var isPrimary = t.StartsWith("SMTP:", StringComparison.Ordinal);
            if (!IsMicrosoftDomain(d))
            {
                if (dropDomain is not null && d == dropDomain) continue;
                if (isPrimary && !keepOldPrimary) continue;
            }
            result.Add(isPrimary ? "smtp:" + t[5..] : t);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task SetTemplatesAsync(string policy, IReadOnlyList<string> templates) =>
        _session.InvokeAsync("Set-EmailAddressPolicy", new Dictionary<string, object?>
        {
            ["Identity"] = policy, ["EnabledEmailAddressTemplates"] = templates.ToArray(),
        });

    public Task ApplyPolicyAsync(string policy) =>
        _session.InvokeAsync("Update-EmailAddressPolicy", new Dictionary<string, object?> { ["Identity"] = policy });

    public Task RemoveDomainAsync(string name) =>
        _session.InvokeAsync("Remove-AcceptedDomain", new Dictionary<string, object?> { ["Identity"] = name, ["Confirm"] = false });

    /// <summary>Everything that still uses a domain: primary and secondary addresses, policies and connectors.</summary>
    public async Task<DomainUsage> CheckUsageAsync(string domain, IProgress<string>? progress = null)
    {
        domain = domain.ToLowerInvariant();
        var usage = new DomainUsage { Domain = domain };

        progress?.Report("Reading every recipient's addresses");
        var recipients = (await _session.InvokeAsync("Get-Recipient",
            new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, quiet: true,
            select: new[] { "Name", "Identity", "RecipientTypeDetails", "PrimarySmtpAddress", "EmailAddresses" })).ToList();
        // System (arbitration) mailboxes aren't in Get-Recipient
        try
        {
            recipients.AddRange(await _session.InvokeAsync("Get-Mailbox",
                new Dictionary<string, object?> { ["Arbitration"] = null, ["ResultSize"] = "Unlimited" }, quiet: true,
                select: new[] { "Name", "Identity", "RecipientTypeDetails", "PrimarySmtpAddress", "EmailAddresses" }));
        }
        catch (Exception ex) { Log.Warn($"Couldn't read the arbitration mailboxes: {ex.Message}"); }

        foreach (var r in recipients)
        {
            var type = r.Str("RecipientTypeDetails");
            var primary = r.Str("PrimarySmtpAddress");
            if (DomainOf(primary) == domain) { usage.Primary.Add((r.Str("Name"), type, primary)); continue; }
            foreach (var a in r.List("EmailAddresses"))
                if (a.StartsWith("smtp:", StringComparison.Ordinal) && DomainOf(a) == domain)
                    usage.Secondary.Add((r.Str("Name"), r.Str("Identity"), type, a[5..]));
        }

        progress?.Report("Checking address policies and connectors");
        foreach (var p in await GetPoliciesAsync())
            if (p.Templates.Any(t => DomainOf(t) == domain)) usage.Policies.Add(p.Name);
        try
        {
            foreach (var c in await _session.InvokeAsync("Get-SendConnector", quiet: true, select: new[] { "Name", "AddressSpaces" }))
                if (c.List("AddressSpaces").Any(a => a.Contains(domain, StringComparison.OrdinalIgnoreCase))) usage.Connectors.Add(c.Str("Name"));
        }
        catch { }
        return usage;
    }

    /// <summary>Removes the leftover secondary addresses in a domain. Returns the failures.</summary>
    public async Task<List<string>> RemoveSecondaryAddressesAsync(DomainUsage usage, IProgress<string>? progress = null)
    {
        var failed = new List<string>();
        var i = 0;
        foreach (var (name, identity, type, address) in usage.Secondary)
        {
            progress?.Report($"Removing addresses ({++i} of {usage.Secondary.Count})");
            var cmdlet = SetCmdletFor(type);
            if (cmdlet is null) { failed.Add($"{name}: {type} isn't handled, remove {address} by hand"); continue; }
            var p = new Dictionary<string, object?>
            {
                ["Identity"] = identity,
                ["EmailAddresses"] = new Hashtable { ["remove"] = new[] { "smtp:" + address } },
            };
            if (type.Equals("ArbitrationMailbox", StringComparison.OrdinalIgnoreCase)) p["Arbitration"] = null;
            try { await _session.InvokeAsync(cmdlet, p, quiet: true); Log.Info($"{name}: removed {address}"); }
            catch (Exception ex) { failed.Add($"{name}: {ex.Message}"); }
        }
        return failed;
    }

    private static string? SetCmdletFor(string type) => type switch
    {
        "UserMailbox" or "SharedMailbox" or "RoomMailbox" or "EquipmentMailbox" or "LinkedMailbox" or "SchedulingMailbox"
            or "DiscoveryMailbox" or "ArbitrationMailbox" => "Set-Mailbox",
        "RemoteUserMailbox" or "RemoteSharedMailbox" or "RemoteRoomMailbox" or "RemoteEquipmentMailbox" => "Set-RemoteMailbox",
        "MailUniversalDistributionGroup" or "MailUniversalSecurityGroup" or "MailNonUniversalGroup" or "RoomList" => "Set-DistributionGroup",
        "DynamicDistributionGroup" => "Set-DynamicDistributionGroup",
        "MailContact" => "Set-MailContact",
        "MailUser" => "Set-MailUser",
        "PublicFolder" => "Set-MailPublicFolder",
        _ => null,
    };
}
