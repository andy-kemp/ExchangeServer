using System.Management.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using ExchangeCertManager.Helpers;

namespace ExchangeCertManager.Models;

public sealed partial class ExchangeServerInfo : ObservableObject
{
    public required string Name { get; init; }
    public required string Fqdn { get; init; }
    public string Version { get; init; } = "";
    public string Roles { get; init; } = "";

    /// <summary>Used by the multi-select server lists.</summary>
    [ObservableProperty] private bool _isSelected = true;

    public static ExchangeServerInfo From(PSObject o) => new()
    {
        Name = o.Str("Name"),
        Fqdn = string.IsNullOrEmpty(o.Str("Fqdn")) ? o.Str("Name") : o.Str("Fqdn"),
        Version = o.Str("AdminDisplayVersion"),
        Roles = o.Str("ServerRole"),
    };

    public override string ToString() => Name;
}

/// <summary>One certificate as it exists on one server.</summary>
public sealed class ServerCertificate
{
    public required string Server { get; init; }
    public required string Thumbprint { get; init; }
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public List<string> Domains { get; init; } = new();
    public string Services { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsSelfSigned { get; init; }
    public bool HasPrivateKey { get; init; }
    public bool PrivateKeyExportable { get; init; }

    public bool IsPending => Status.Equals("PendingRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>"None" means no services assigned.</summary>
    public bool HasServices => !string.IsNullOrWhiteSpace(Services) && !Services.Equals("None", StringComparison.OrdinalIgnoreCase);

    public static ServerCertificate From(string server, PSObject o) => new()
    {
        Server = server,
        Thumbprint = o.Str("Thumbprint"),
        Subject = o.Str("Subject"),
        Issuer = o.Str("Issuer"),
        FriendlyName = o.Str("FriendlyName"),
        NotBefore = o.Date("NotBefore"),
        NotAfter = o.Date("NotAfter"),
        Domains = o.List("CertificateDomains"),
        Services = o.Str("Services"),
        Status = o.Str("Status"),
        IsSelfSigned = o.Bool("IsSelfSigned"),
        HasPrivateKey = o.Bool("HasPrivateKey"),
        PrivateKeyExportable = o.Bool("PrivateKeyExportable"),
    };
}

/// <summary>A certificate (by thumbprint) and where it's installed across the organisation.</summary>
public sealed class CertificateGroup
{
    public required string Thumbprint { get; init; }
    public required List<ServerCertificate> Installs { get; init; }
    public bool IsAuthCertificate { get; set; }

    private ServerCertificate First => Installs[0];
    public string Subject => First.Subject;
    public string CommonName => SubjectCn(First.Subject);
    public string Issuer => First.Issuer;
    public string IssuerShort => SubjectCn(First.Issuer) is { Length: > 0 } cn ? cn : First.Issuer;
    public string FriendlyName => First.FriendlyName;
    public DateTime? NotAfter => First.NotAfter;
    public List<string> Domains => First.Domains;
    public string DomainsText => string.Join(", ", Domains);
    public bool IsSelfSigned => First.IsSelfSigned;
    public bool IsPending => Installs.All(i => i.IsPending);
    public bool PrivateKeyExportable => Installs.Any(i => i.PrivateKeyExportable);
    public string ServersText => string.Join(", ", Installs.Select(i => i.Server));
    public string ServicesText => string.Join("; ", Installs.Where(i => i.HasServices).Select(i => $"{i.Server}: {i.Services}"));
    public bool HasServices => Installs.Any(i => i.HasServices);

    public int? DaysLeft => NotAfter is { } d ? (int)Math.Floor((d - DateTime.Now).TotalDays) : null;

    /// <summary>Ok / Warning / Error / Info, for the pill style.</summary>
    public string ExpiryState => IsPending ? "Info"
        : DaysLeft is null ? "Info"
        : DaysLeft < 0 ? "Error"
        : DaysLeft < 30 ? "Error"
        : DaysLeft < 60 ? "Warning"
        : "Ok";

    public string ExpiryText => IsPending ? "Pending request"
        : DaysLeft is null ? "Unknown"
        : DaysLeft < 0 ? $"Expired {-DaysLeft}d ago"
        : $"{DaysLeft} days";

    public string Badges
    {
        get
        {
            var b = new List<string>();
            if (IsAuthCertificate) b.Add("Exchange Auth");
            if (IsSelfSigned) b.Add("Self-signed");
            if (IsPending) b.Add("Pending");
            return string.Join(" · ", b);
        }
    }

    public static string SubjectCn(string dn)
    {
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..];
        }
        return "";
    }
}

public enum HealthSeverity { Ok, Info, Warning, Error }

public sealed record HealthItem(HealthSeverity Severity, string Area, string Server, string Title, string Detail)
{
    public string SeverityText => Severity.ToString();
    public HealthFix? Fix { get; init; }
    public bool HasFix => Fix is not null;
}

public enum AcmeChallengeType { Http01, Dns01Manual }

/// <summary>Saved definition used by the scheduled Let's Encrypt renewal.</summary>
public sealed class AcmeRenewal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public List<string> Domains { get; set; } = new();
    public string Email { get; set; } = "";
    public bool Staging { get; set; }
    public string ConnectServer { get; set; } = "";
    public List<string> Servers { get; set; } = new();
    public Dictionary<string, string> Webroots { get; set; } = new();
    public List<string> Services { get; set; } = new() { "IIS" };
    public bool RemoveOldCertificate { get; set; } = true;
    public string? LastThumbprint { get; set; }
    public DateTime? NotAfter { get; set; }
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }

    public string DomainsText => string.Join(", ", Domains);
    public string ServersText => string.Join(", ", Servers);
}

public sealed record VdirKind(string Key, string Label, string GetCommand, string SetCommand, string Path);

/// <summary>One virtual directory (or Outlook Anywhere / the Autodiscover SCP) on one server.</summary>
public sealed class VdirEntry
{
    public required string Server { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public required string Identity { get; init; }
    public string Internal { get; init; } = "";
    public string External { get; init; } = "";

    public string InternalText => string.IsNullOrWhiteSpace(Internal) ? "(none)" : Internal;
    public string ExternalText => Kind == "Autodiscover" ? "n/a" : string.IsNullOrWhiteSpace(External) ? "(none)" : External;
}

public sealed class NamespacePlan
{
    public string InternalHost { get; init; } = "";
    public string ExternalHost { get; init; } = "";
    public string AutodiscoverHost { get; init; } = "";
    public bool SetInternal { get; init; } = true;
    public bool SetExternal { get; init; } = true;
    public bool EcpExternalBlank { get; init; }
    public IReadOnlyCollection<string> Servers { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> Kinds { get; init; } = Array.Empty<string>();
}

public sealed record VdirChange(VdirEntry Entry, string Command, Dictionary<string, object?> Parameters, string Before, string After)
{
    public string CommandText => Services.ExchangeSession.Describe(Command, Parameters);
}

public enum HealthFixKind { RenewAuthCertificate, ClearPreviousAuthCertificate, DeployAuthCertificate, UpdateConnectorCertificate }

/// <summary>A one-click fix offered on a health check result.</summary>
public sealed record HealthFix(HealthFixKind Kind, string Label)
{
    public string? ConnectorType { get; init; }      // "Send" or "Receive"
    public string? ConnectorIdentity { get; init; }
    public string? ConnectorName { get; init; }
    public string? OldTlsName { get; init; }
    public IReadOnlyList<string> Servers { get; init; } = Array.Empty<string>();
    public string? Thumbprint { get; init; }
}
