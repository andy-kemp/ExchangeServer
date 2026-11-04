using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

public sealed partial class VdirKindPick : ObservableObject
{
    public VdirKindPick(string key, string label, bool selected = true)
    {
        Key = key;
        Label = label;
        _isSelected = selected;
    }

    public string Key { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class UrlsViewModel : PageViewModel
{
    public UrlsViewModel(MainViewModel main) : base(main)
    {
        foreach (var k in VirtualDirectoryService.UrlKinds) Kinds.Add(new VdirKindPick(k.Key, k.Key == "OWA" ? "OWA" : k.Key));
        Kinds.Add(new VdirKindPick(VirtualDirectoryService.OutlookAnywhere, "Outlook Anywhere"));
        Kinds.Add(new VdirKindPick(VirtualDirectoryService.Autodiscover, "Autodiscover SCP"));
    }

    public override string Title => "Virtual directories";
    public override string Subtitle => "The URLs clients use, on every server: OWA, ECP, EWS, ActiveSync, OAB, MAPI, Outlook Anywhere and the Autodiscover SCP. Set one namespace everywhere, check the certificate covers it, then apply.";

    public ObservableCollection<VdirEntry> Entries { get; } = new();
    public ObservableCollection<SelectableServer> Servers { get; } = new();
    public ObservableCollection<VdirKindPick> Kinds { get; } = new();
    public ObservableCollection<VdirChange> Changes { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EffectiveExternal))] private string _internalHost = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EffectiveExternal))] private string _externalHost = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EffectiveExternal))] private bool _sameAsInternal = true;
    [ObservableProperty] private string _autodiscoverHost = "";
    [ObservableProperty] private bool _setInternal = true;
    [ObservableProperty] private bool _setExternal = true;
    [ObservableProperty] private bool _ecpExternalBlank;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreview))] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _previewReady;

    [ObservableProperty] private string _coverage = "";
    [ObservableProperty] private string _coverageState = "Info";
    [ObservableProperty] private string _previewSummary = "";

    public bool HasPreview => PreviewReady;
    public string EffectiveExternal => SameAsInternal ? InternalHost : ExternalHost;
    private bool _loaded;

    public override void OnShown()
    {
        if (Servers.Count != State.Servers.Count)
        {
            Servers.Clear();
            foreach (var s in State.Servers) Servers.Add(new SelectableServer(s));
        }
        if (!_loaded && State.IsConnected && !State.IsDemo && !State.IsBusy) RefreshCommand.Execute(null);
    }

    // Any edit makes the preview stale.
    partial void OnInternalHostChanged(string value) => PreviewReady = false;
    partial void OnExternalHostChanged(string value) => PreviewReady = false;
    partial void OnSameAsInternalChanged(bool value) => PreviewReady = false;
    partial void OnAutodiscoverHostChanged(string value) => PreviewReady = false;
    partial void OnSetInternalChanged(bool value) => PreviewReady = false;
    partial void OnSetExternalChanged(bool value) => PreviewReady = false;
    partial void OnEcpExternalBlankChanged(bool value) => PreviewReady = false;

    [RelayCommand]
    private Task RefreshAsync() => State.RunAsync("Reading the virtual directories", async () =>
    {
        if (State.Vdirs is null) return;
        var entries = await State.Vdirs.GetAsync(State.Servers);
        Load(entries);
    });

    /// <summary>Fills the grid and, the first time, suggests a namespace from what's configured.</summary>
    public void Load(IEnumerable<VdirEntry> entries)
    {
        Entries.Clear();
        foreach (var e in entries.OrderBy(e => e.Server).ThenBy(e => e.Kind == VirtualDirectoryService.Autodiscover ? 9 : e.Kind == VirtualDirectoryService.OutlookAnywhere ? 8 : 0).ThenBy(e => e.Kind))
            Entries.Add(e);
        _loaded = true;
        PreviewReady = false;

        if (InternalHost.Length == 0) Suggest();
    }

    private void Suggest()
    {
        static string? MostCommon(IEnumerable<string> hosts) => hosts.Where(h => h.Length > 0)
            .GroupBy(h => h).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var ext = MostCommon(Entries.Where(e => e.Kind != VirtualDirectoryService.Autodiscover).Select(e => VirtualDirectoryService.HostOf(e.External)));
        var serverNames = State.Servers.Select(s => s.Fqdn.ToLowerInvariant()).ToHashSet();
        var intl = MostCommon(Entries.Where(e => e.Kind != VirtualDirectoryService.Autodiscover).Select(e => VirtualDirectoryService.HostOf(e.Internal))
            .Where(h => !serverNames.Contains(h) && !State.Servers.Any(s => s.Name.Equals(h, StringComparison.OrdinalIgnoreCase))));
        var ns = ext ?? intl ?? "";
        InternalHost = intl ?? ns;
        ExternalHost = ext ?? ns;
        SameAsInternal = string.Equals(InternalHost, ExternalHost, StringComparison.OrdinalIgnoreCase);
        var dot = ns.IndexOf('.');
        AutodiscoverHost = MostCommon(Entries.Where(e => e.Kind == VirtualDirectoryService.Autodiscover).Select(e => VirtualDirectoryService.HostOf(e.Internal))
                               .Where(h => !serverNames.Contains(h)))
                           ?? (dot > 0 ? "autodiscover" + ns[dot..] : "");
    }

    private NamespacePlan? BuildPlan(out string? error)
    {
        error = null;
        var intl = InternalHost.Trim().ToLowerInvariant();
        var ext = EffectiveExternal.Trim().ToLowerInvariant();
        var auto = AutodiscoverHost.Trim().ToLowerInvariant();
        if (!SetInternal && !SetExternal) { error = "Choose internal URLs, external URLs or both."; return null; }
        var names = new List<string>();
        if (SetInternal) names.Add(intl);
        if (SetExternal) names.Add(ext);
        if (Kinds.Any(k => k.Key == VirtualDirectoryService.Autodiscover && k.IsSelected) && SetInternal && auto.Length > 0) names.Add(auto);
        if (ValidateNames(names, allowWildcard: false) is { } bad) { error = bad; return null; }
        var servers = Servers.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        var kinds = Kinds.Where(k => k.IsSelected).Select(k => k.Key).ToList();
        if (servers.Count == 0 || kinds.Count == 0) { error = "Tick at least one server and one service."; return null; }

        return new NamespacePlan
        {
            InternalHost = intl, ExternalHost = ext, AutodiscoverHost = auto,
            SetInternal = SetInternal, SetExternal = SetExternal, EcpExternalBlank = EcpExternalBlank,
            Servers = servers, Kinds = kinds,
        };
    }

    [RelayCommand]
    private void Preview()
    {
        var plan = BuildPlan(out var error);
        if (plan is null) { AppState.Inform(error!); return; }

        Changes.Clear();
        foreach (var c in VirtualDirectoryService.Plan(Entries, plan)) Changes.Add(c);

        // Does the IIS certificate on each server cover the names?
        var names = new List<string>();
        if (plan.SetInternal) names.Add(plan.InternalHost);
        if (plan.SetExternal) names.Add(plan.ExternalHost);
        if (plan.Kinds.Contains(VirtualDirectoryService.Autodiscover) && plan.SetInternal && plan.AutodiscoverHost.Length > 0) names.Add(plan.AutodiscoverHost);
        names = names.Distinct().ToList();

        var problems = new List<string>();
        foreach (var server in plan.Servers)
        {
            var iis = State.Inventory.FirstOrDefault(g => !g.IsSelfSigned && g.Installs.Any(i =>
                i.Server.Equals(server, StringComparison.OrdinalIgnoreCase) && i.Services.Contains("IIS", StringComparison.OrdinalIgnoreCase)));
            if (iis is null) { problems.Add($"{server}: no CA-issued certificate is assigned to IIS"); continue; }
            var missing = names.Where(n => !HealthService.Covers(iis.Domains, n)).ToList();
            if (missing.Count > 0) problems.Add($"{server}: {iis.CommonName} doesn't cover {string.Join(", ", missing)}");
        }
        Coverage = problems.Count == 0
            ? $"The IIS certificate on every ticked server covers {string.Join(", ", names)}."
            : "Clients will get certificate warnings for these names until the certificate covers them:\n" + string.Join("\n", problems);
        CoverageState = problems.Count == 0 ? "Ok" : "Warning";
        if (InternalNameWarning(names) is { } internalWarning && plan.SetExternal)
        {
            Coverage += "\n" + internalWarning;
            CoverageState = "Warning";
        }

        PreviewSummary = Changes.Count == 0
            ? "Nothing to change: every ticked URL already matches."
            : $"{Changes.Count} change(s) across {Changes.Select(c => c.Entry.Server).Distinct().Count()} server(s). Nothing has changed yet.";
        PreviewReady = true;
    }

    private bool CanApply() => PreviewReady && Changes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!State.RequireLive()) return;
        var warn = CoverageState == "Warning" ? "\n\nThe certificate doesn't cover every name yet (see the preview)." : "";
        if (!AppState.Confirm($"Apply {Changes.Count} change(s) to the virtual directories?{warn}\n\nOutlook and phones pick up new URLs through Autodiscover, which can take a few hours.", "Apply URLs"))
            return;

        var changes = Changes.ToList();
        List<(VdirChange Change, string Error)> failed = new();
        var ok = await State.RunAsync("Applying the URLs", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            failed = await State.Vdirs!.ApplyAsync(changes, progress);
            State.SetBusyText("Reading them back");
            Load(await State.Vdirs.GetAsync(State.Servers));
        });

        if (!ok) return;
        if (failed.Count == 0)
            AppState.Inform("Done. Update DNS (internal and public) to point the names at Exchange or your load balancer if you haven't already, then run the Health check.");
        else
            AppState.Inform($"{changes.Count - failed.Count} of {changes.Count} applied. These failed:\n\n" +
                            string.Join("\n", failed.Select(f => $"{f.Change.Entry.Server} {f.Change.Entry.Label}: {f.Error}")));
    }
}
