using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

/// <summary>One step of the "change the default domain" plan, shown before anything runs.</summary>
public sealed record DomainStep(string Text, string Command);

public sealed partial class DomainsViewModel : PageViewModel
{
    public DomainsViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Accepted domains";
    public override string Subtitle => "The domains Exchange accepts mail for, and the address policy that gives everyone their addresses. Change the default domain and retire an old one (such as the AD domain) in the order Exchange needs, with a preview first. The Microsoft 365 (onmicrosoft.com) domains are never changed.";

    public ObservableCollection<AcceptedDomainInfo> Domains { get; } = new();
    public ObservableCollection<AddressPolicyInfo> Policies { get; } = new();
    public ObservableCollection<string> DomainNames { get; } = new();
    public ObservableCollection<DomainStep> Steps { get; } = new();

    [ObservableProperty] private string _newDomain = "";
    [ObservableProperty] private string _newDomainType = "Authoritative";
    public IReadOnlyList<string> DomainTypes { get; } = new[] { "Authoritative", "InternalRelay", "ExternalRelay" };

    // Change the default domain
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlan))] [NotifyCanExecuteChangedFor(nameof(ApplyPlanCommand))]
    private bool _planReady;
    [ObservableProperty] private string? _targetDomain;
    [ObservableProperty] private bool _keepOldAsSecondary = true;
    [ObservableProperty] private string? _dropDomain;
    [ObservableProperty] private bool _applyPolicyNow = true;
    [ObservableProperty] private string _planSummary = "";
    public bool HasPlan => PlanReady;

    // Retire a domain
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUsage))] private DomainUsage? _usage;
    [ObservableProperty] private string? _retireDomain;
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private string _usageState = "Info";
    public bool HasUsage => Usage is not null;
    public bool CanRemoveAddresses => Usage is { Secondary.Count: > 0 };
    public bool CanRemoveDomain => Usage is { CanRemove: true };

    private bool _loaded;
    private List<string> _planTemplates = new();
    private AddressPolicyInfo? _planPolicy;

    partial void OnTargetDomainChanged(string? value) => PlanReady = false;
    partial void OnKeepOldAsSecondaryChanged(bool value) => PlanReady = false;
    partial void OnDropDomainChanged(string? value) => PlanReady = false;
    partial void OnApplyPolicyNowChanged(bool value) => PlanReady = false;
    partial void OnRetireDomainChanged(string? value) { Usage = null; UsageText = ""; }
    partial void OnUsageChanged(DomainUsage? value)
    {
        OnPropertyChanged(nameof(CanRemoveAddresses));
        OnPropertyChanged(nameof(CanRemoveDomain));
    }

    public override void OnShown()
    {
        if (!_loaded && State.IsConnected && !State.IsDemo && !State.IsBusy) RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private Task RefreshAsync() => State.RunAsync("Reading accepted domains and address policies", async () =>
    {
        if (State.Domains is null) return;
        Load(await State.Domains.GetDomainsAsync(), await State.Domains.GetPoliciesAsync());
    });

    public void Load(IEnumerable<AcceptedDomainInfo> domains, IEnumerable<AddressPolicyInfo> policies)
    {
        Domains.Clear();
        foreach (var d in domains) Domains.Add(d);
        Policies.Clear();
        foreach (var p in policies) Policies.Add(p);
        DomainNames.Clear();
        foreach (var d in Domains.Where(d => !d.IsMicrosoft)) DomainNames.Add(d.DomainName);
        _loaded = true;
        PlanReady = false;

        // Suggest: the current default as the target unless the URLs point somewhere else; the AD-looking domain to retire
        var def = Domains.FirstOrDefault(d => d.Default)?.DomainName;
        TargetDomain ??= def;
        RetireDomain ??= DomainNames.FirstOrDefault(d => d != TargetDomain && LooksInternal(d));
    }

    /// <summary>.local, ad., corp. and the like: the usual AD domains that aren't used for mail.</summary>
    public static bool LooksInternal(string domain) =>
        domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".corp", StringComparison.OrdinalIgnoreCase)
        || domain.StartsWith("ad.", StringComparison.OrdinalIgnoreCase) || domain.StartsWith("corp.", StringComparison.OrdinalIgnoreCase)
        || domain.StartsWith("int.", StringComparison.OrdinalIgnoreCase) || domain.StartsWith("internal.", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- add a domain

    [RelayCommand]
    private async Task AddDomainAsync()
    {
        if (!State.RequireLive()) return;
        var d = NewDomain.Trim().TrimEnd('.').ToLowerInvariant();
        if (ValidateNames(new[] { d }, allowWildcard: false) is { } bad) { AppState.Inform(bad); return; }
        if (Domains.Any(x => x.DomainName == d)) { AppState.Inform($"{d} is already an accepted domain."); return; }
        if (DomainService.IsMicrosoftDomain(d)) { AppState.Inform("Microsoft 365 domains are added by the Hybrid Configuration Wizard, not here."); return; }
        if (!AppState.Confirm($"Add {d} as an {NewDomainType} accepted domain?\n\nNew-AcceptedDomain -Name {d} -DomainName {d} -DomainType {NewDomainType}")) return;
        if (await State.RunAsync($"Adding {d}", async () =>
            {
                await State.Domains!.AddDomainAsync(d, NewDomainType);
                Load(await State.Domains.GetDomainsAsync(), await State.Domains.GetPoliciesAsync());
            }))
        {
            NewDomain = "";
            Log.Success($"{d} added. Remember its MX and SPF records in public DNS if it's going to receive mail.");
        }
    }

    // ---------------------------------------------------------------- change the default domain

    [RelayCommand]
    private void PreviewPlan()
    {
        var target = TargetDomain?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target)) { AppState.Inform("Choose the domain that should be everyone's primary address."); return; }
        var policy = Policies.FirstOrDefault(p => p.IsDefault);
        if (policy is null) { AppState.Inform("There's no default email address policy to change."); return; }
        var drop = string.IsNullOrEmpty(DropDomain) ? null : DropDomain.ToLowerInvariant();
        if (drop == target) { AppState.Inform("The domain to drop can't be the new primary domain."); return; }

        Steps.Clear();
        var domain = Domains.First(d => d.DomainName == target);
        if (!domain.Default)
            Steps.Add(new DomainStep($"Make {target} the default accepted domain", $"Set-AcceptedDomain -Identity '{domain.Name}' -MakeDefault $true"));

        _planPolicy = policy;
        _planTemplates = DomainService.NewTemplates(policy, target, KeepOldAsSecondary, drop);
        var changed = !_planTemplates.SequenceEqual(policy.Templates, StringComparer.Ordinal);
        if (changed)
            Steps.Add(new DomainStep(
                $"Set the '{policy.Name}' policy to {string.Join(", ", _planTemplates)}",
                $"Set-EmailAddressPolicy -Identity '{policy.Name}' -EnabledEmailAddressTemplates {string.Join(",", _planTemplates.Select(t => $"'{t}'"))}"));
        if (ApplyPolicyNow && (changed || !domain.Default))
            Steps.Add(new DomainStep(
                $"Apply the policy: everyone it covers gets {_planTemplates[0][5..]} as their primary address (existing addresses are kept)",
                $"Update-EmailAddressPolicy -Identity '{policy.Name}'"));

        PlanSummary = Steps.Count == 0
            ? $"Nothing to change: {target} is already the default domain and the default policy already uses it."
            : $"{Steps.Count} step(s), in this order. Nothing has changed yet." +
              (ApplyPolicyNow ? " Applying the policy updates every mailbox, group and contact it covers, which can take a while in a big organisation." : " The policy won't be applied yet: run Update-EmailAddressPolicy later, or tick 'Apply it now'.");
        PlanReady = true;
    }

    private bool CanApplyPlan() => PlanReady && Steps.Count > 0;

    [RelayCommand(CanExecute = nameof(CanApplyPlan))]
    private async Task ApplyPlanAsync()
    {
        if (!State.RequireLive() || _planPolicy is null) return;
        if (!AppState.Confirm($"Run these {Steps.Count} step(s)?\n\n" + string.Join("\n", Steps.Select((s, i) => $"{i + 1}. {s.Text}")), "Change the default domain")) return;
        var target = TargetDomain!.ToLowerInvariant();
        var policy = _planPolicy;
        var templates = _planTemplates;
        var steps = Steps.ToList();
        var ok = await State.RunAsync("Changing the default domain", async () =>
        {
            var d = State.Domains!;
            var domain = Domains.First(x => x.DomainName == target);
            if (!domain.Default) { State.SetBusyText($"Making {target} the default"); await d.MakeDefaultAsync(domain.Name); }
            if (steps.Any(s => s.Command.StartsWith("Set-EmailAddressPolicy", StringComparison.Ordinal)))
            { State.SetBusyText("Updating the address policy"); await d.SetTemplatesAsync(policy.Name, templates); }
            if (steps.Any(s => s.Command.StartsWith("Update-EmailAddressPolicy", StringComparison.Ordinal)))
            { State.SetBusyText("Applying the address policy (this can take a while)"); await d.ApplyPolicyAsync(policy.Name); }
            Load(await d.GetDomainsAsync(), await d.GetPoliciesAsync());
        });
        if (ok)
        {
            PlanReady = false;
            AppState.Inform($"Done. {target} is now the default domain and everyone's primary address.\n\n" +
                            "To retire the old domain, use 'Retire a domain' below: it checks who still uses it first.");
        }
    }

    // ---------------------------------------------------------------- retire a domain

    [RelayCommand]
    private async Task CheckUsageAsync()
    {
        if (!State.RequireLive()) return;
        var d = RetireDomain?.ToLowerInvariant();
        if (string.IsNullOrEmpty(d)) { AppState.Inform("Choose the domain to retire."); return; }
        if (Domains.FirstOrDefault(x => x.DomainName == d) is { Default: true }) { AppState.Inform($"{d} is the default domain. Make another domain the default first (Change the default domain, above)."); return; }
        DomainUsage? usage = null;
        await State.RunAsync($"Checking who uses {d}", async () =>
            usage = await State.Domains!.CheckUsageAsync(d, new Progress<string>(State.SetBusyText)));
        if (usage is null) return;
        Usage = usage;
        DescribeUsage();
    }

    private void DescribeUsage()
    {
        var u = Usage!;
        var lines = new List<string>();
        if (u.Policies.Count > 0) lines.Add($"Address policies that still use it: {string.Join(", ", u.Policies)}. Change the default domain above with this domain in 'Drop', or edit these policies, first.");
        if (u.Primary.Count > 0)
            lines.Add($"{u.Primary.Count} recipient(s) still have it as their primary address: " +
                      string.Join(", ", u.Primary.Take(8).Select(p => $"{p.Name} ({p.Address})")) + (u.Primary.Count > 8 ? "…" : "") +
                      ". Apply the address policy, or give them another primary address, first.");
        if (u.Secondary.Count > 0)
            lines.Add($"{u.Secondary.Count} recipient(s) have a secondary address in it (for example {string.Join(", ", u.Secondary.Take(3).Select(s => s.Address))}). " +
                      "Mail to those addresses stops once the domain is removed. Remove the addresses first if nothing uses them.");
        if (u.Connectors.Count > 0) lines.Add($"Send connectors that mention it: {string.Join(", ", u.Connectors)}. Check them by hand.");
        if (lines.Count == 0) lines.Add($"Nothing uses {u.Domain}: no policies, no primary or secondary addresses. It can be removed.");
        else if (u.CanRemove) lines.Add($"It can be removed now; the warnings above are worth a look first.");

        UsageText = string.Join("\n\n", lines);
        UsageState = !u.CanRemove ? "Error" : lines.Count > 1 || u.Secondary.Count > 0 || u.Connectors.Count > 0 ? "Warning" : "Ok";
        OnPropertyChanged(nameof(CanRemoveAddresses));
        OnPropertyChanged(nameof(CanRemoveDomain));
    }

    [RelayCommand]
    private async Task RemoveAddressesAsync()
    {
        if (!State.RequireLive() || Usage is not { Secondary.Count: > 0 } u) return;
        if (!AppState.Confirm($"Remove {u.Secondary.Count} secondary address(es) in {u.Domain}?\n\n" +
                              string.Join("\n", u.Secondary.Take(12).Select(s => $"{s.Name}: {s.Address}")) + (u.Secondary.Count > 12 ? "\n…" : "") +
                              "\n\nPrimary addresses aren't touched. Mail to these addresses will bounce afterwards.", "Remove addresses")) return;
        List<string> failed = new();
        await State.RunAsync("Removing addresses", async () =>
        {
            failed = await State.Domains!.RemoveSecondaryAddressesAsync(u, new Progress<string>(State.SetBusyText));
            State.SetBusyText("Checking again");
            Usage = await State.Domains.CheckUsageAsync(u.Domain);
        });
        if (Usage is not null) DescribeUsage();
        if (failed.Count > 0) AppState.Inform($"{failed.Count} couldn't be changed:\n\n" + string.Join("\n", failed.Take(15)));
    }

    [RelayCommand]
    private async Task RemoveDomainAsync()
    {
        if (!State.RequireLive() || Usage is not { CanRemove: true } u) return;
        var domain = Domains.FirstOrDefault(d => d.DomainName == u.Domain);
        if (domain is null) return;
        if (domain.IsMicrosoft) { AppState.Inform("Microsoft 365 domains aren't removed here."); return; }
        var extra = u.Secondary.Count > 0 ? $"\n\n{u.Secondary.Count} recipient(s) still have a secondary address in it; mail to those will bounce." : "";
        if (!AppState.Confirm($"Remove the accepted domain {u.Domain}?{extra}\n\nRemove-AcceptedDomain -Identity '{domain.Name}'", "Remove accepted domain")) return;
        if (await State.RunAsync($"Removing {u.Domain}", async () =>
            {
                await State.Domains!.RemoveDomainAsync(domain.Name);
                Load(await State.Domains.GetDomainsAsync(), await State.Domains.GetPoliciesAsync());
            }))
        {
            Log.Success($"{u.Domain} removed from the accepted domains");
            RetireDomain = null;
            Usage = null;
        }
    }
}
