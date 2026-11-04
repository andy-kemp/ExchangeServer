using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

/// <summary>A server with a tick box, used by every multi-server picker.</summary>
public sealed partial class SelectableServer : ObservableObject
{
    public SelectableServer(ExchangeServerInfo server, bool selected = true)
    {
        Server = server;
        _isSelected = selected;
    }

    public ExchangeServerInfo Server { get; }
    public string Name => Server.Name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>IIS / SMTP / IMAP / POP tick boxes.</summary>
public sealed partial class ServiceSelection : ObservableObject
{
    [ObservableProperty] private bool _iis = true;
    [ObservableProperty] private bool _smtp;
    [ObservableProperty] private bool _imap;
    [ObservableProperty] private bool _pop;

    public List<string> Selected()
    {
        var list = new List<string>();
        if (Iis) list.Add("IIS");
        if (Smtp) list.Add("SMTP");
        if (Imap) list.Add("IMAP");
        if (Pop) list.Add("POP");
        return list;
    }

    public void Set(IEnumerable<string> services)
    {
        var s = services.Select(x => x.Trim().ToUpperInvariant()).ToHashSet();
        Iis = s.Contains("IIS");
        Smtp = s.Contains("SMTP");
        Imap = s.Contains("IMAP");
        Pop = s.Contains("POP");
    }
}

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(MainViewModel main) => Main = main;

    public MainViewModel Main { get; }
    public AppState State => AppState.Current;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    /// <summary>Called each time the page is shown.</summary>
    public virtual void OnShown() { }

    protected static ObservableCollection<SelectableServer> ServerPicks(IEnumerable<ExchangeServerInfo> servers, Func<ExchangeServerInfo, bool>? selected = null) =>
        new(servers.Select(s => new SelectableServer(s, selected?.Invoke(s) ?? true)));

    /// <summary>Splits a textbox of names (lines, commas, semicolons or spaces) into a clean list.</summary>
    protected static List<string> SplitNames(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    protected static string? ValidateNames(IEnumerable<string> names, bool allowWildcard)
    {
        foreach (var n in names)
        {
            var host = allowWildcard && n.StartsWith("*.", StringComparison.Ordinal) ? n[2..] : n;
            if (n.Contains('*') && !allowWildcard) return $"'{n}' is a wildcard. Choose the wildcard option.";
            if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
                return $"'{n}' isn't a valid DNS name.";
        }
        return null;
    }

    /// <summary>Public CAs refuse internal names. Worth a warning, not a block (internal CAs are fine).</summary>
    protected static string? InternalNameWarning(IEnumerable<string> names)
    {
        var internalNames = names.Where(n => n.EndsWith(".local") || n.EndsWith(".lan") || n.EndsWith(".internal") || n.EndsWith(".corp")).ToList();
        return internalNames.Count == 0 ? null
            : $"Public CAs won't issue for {string.Join(", ", internalNames)}. That's fine for an internal CA; otherwise change those URLs to public names.";
    }

    protected async Task<List<string>> SuggestNamesAsync(string server)
    {
        var certs = State.Certs!;
        var hosts = await certs.GetClientHostnamesAsync(server);

        // autodiscover.<domain> for the default accepted domain and the domains the URLs actually use. Other
        // accepted domains (often the AD domain, such as ad.contoso.com) don't get one unless something points there.
        var accepted = await certs.GetAcceptedDomainsAsync();
        var configured = hosts.ToList();
        for (var i = 0; i < accepted.Count; i++)
        {
            var d = accepted[i].ToLowerInvariant();
            if (i == 0 || configured.Any(h => h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
                hosts.Add($"autodiscover.{d}");
        }

        // A misspelt name in the Exchange configuration is suggested as the right one, with a warning
        foreach (var h in hosts.ToList())
        {
            var first = h.Split('.')[0];
            if (!first.Equals("autodiscover", StringComparison.OrdinalIgnoreCase) && Distance(first.ToLowerInvariant(), "autodiscover") <= 2)
            {
                var fixedName = "autodiscover" + h[first.Length..];
                hosts.Remove(h);
                hosts.Add(fixedName);
                Log.Warn($"{h} looks like a typo for {fixedName}. It's in the Exchange configuration on {server}: fix it on the Virtual directories page (the Autodiscover SCP or a URL), or clients will look for the wrong name.");
            }
        }

        Log.Info($"Suggested names from {server}: {string.Join(", ", hosts)}");
        return hosts.ToList();
    }

    /// <summary>Levenshtein distance, for spotting typos in well-known names.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    protected static string PickCommonName(IReadOnlyList<string> names) =>
        names.FirstOrDefault(n => n.StartsWith("mail.", StringComparison.OrdinalIgnoreCase))
        ?? names.FirstOrDefault(n => !n.StartsWith("autodiscover.", StringComparison.OrdinalIgnoreCase))
        ?? names.FirstOrDefault() ?? "";
}
