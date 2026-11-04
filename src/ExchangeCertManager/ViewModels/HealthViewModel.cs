using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using ExchangeCertManager.Views;

namespace ExchangeCertManager.ViewModels;

public sealed partial class HealthViewModel : PageViewModel
{
    public HealthViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Health check";
    public override string Subtitle => "Expiry, the Exchange Auth certificate, SAN coverage of your configured URLs, connectors pinned to old certificates, and clean-up candidates.";

    public ObservableCollection<HealthItem> Items { get; } = new();

    [ObservableProperty] private string _summary = "Run the checks to see how your certificates look.";
    [ObservableProperty] private DateTime? _lastRun;

    public override void OnShown()
    {
        if (LastRun is null && State.IsConnected && !State.IsBusy) RunCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        await State.RunAsync("Running health checks", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            var items = await State.Health!.RunAsync(State.Servers.ToList(), progress);
            Items.Clear();
            foreach (var i in items) Items.Add(i);

            var errors = items.Count(i => i.Severity == HealthSeverity.Error);
            var warnings = items.Count(i => i.Severity == HealthSeverity.Warning);
            Summary = errors + warnings == 0
                ? "All good."
                : $"{errors} problem(s) to fix and {warnings} warning(s).";
            LastRun = DateTime.Now;
        });
    }

    [RelayCommand]
    private async Task FixAsync(HealthItem? item)
    {
        if (item?.Fix is not { } fix || !State.RequireLive()) return;
        var all = State.Servers.ToList();
        var progress = new Progress<string>(State.SetBusyText);

        switch (fix.Kind)
        {
            case HealthFixKind.RenewAuthCertificate:
            {
                var broken = item.Severity == HealthSeverity.Error || item.Title.StartsWith("No Exchange Auth", StringComparison.Ordinal);
                var choice = AuthRenewDialog.Show(all.Select(s => s.Name).ToList(), broken);
                if (choice is not { } c) return;
                string? thumb = null;
                var ok = await State.RunAsync("Renewing the Auth certificate", async () =>
                {
                    thumb = await State.Fixes!.RenewAuthCertificateAsync(c.Server, all, c.Immediate, progress);
                    await State.RefreshAsync();
                });
                if (ok)
                    AppState.Inform(c.Immediate
                        ? $"The new Auth certificate ({thumb}) is active. If you're hybrid, run the Hybrid Configuration Wizard again."
                        : $"The new Auth certificate ({thumb}) is staged and becomes active on its own from {DateTime.Now.Add(FixService.StagingDelay):dddd d MMM HH:mm}. " +
                          "Afterwards, come back to the Health check to clear the previous one, and re-run the Hybrid Configuration Wizard if you're hybrid.");
                break;
            }

            case HealthFixKind.ClearPreviousAuthCertificate:
                if (!AppState.Confirm("Clear the previous Auth certificate from the Auth configuration and recycle the OWA and ECP app pools on every server?\n\nDo this once the new certificate has been active for a while.", "Clear previous Auth certificate"))
                    return;
                await State.RunAsync("Clearing the previous Auth certificate", () => State.Fixes!.ClearPreviousAuthCertificateAsync(all, progress));
                break;

            case HealthFixKind.DeployAuthCertificate:
            {
                var group = State.Inventory.FirstOrDefault(g => g.Thumbprint.Equals(fix.Thumbprint, StringComparison.OrdinalIgnoreCase));
                if (group is null) { AppState.Inform("Refresh the Certificates page first."); return; }
                Main.Deploy.Prefill(group);
                Main.Navigate("deploy");
                return;
            }

            case HealthFixKind.UpdateConnectorCertificate:
            {
                var cert = FixService.PickConnectorCertificate(State.Inventory, fix);
                if (cert is null)
                {
                    AppState.Inform($"There's no valid CA-issued certificate on {string.Join(", ", fix.Servers)} to point it at. Install the new certificate there first (Complete request, Import PFX or Deploy).");
                    return;
                }
                var subjectChanged = !CertificateGroup.SubjectCn(cert.Subject).Equals(CertificateGroup.SubjectCn(fix.OldTlsName?.Split("<S>").Last() ?? ""), StringComparison.OrdinalIgnoreCase);
                var exo = fix.ConnectorType == "Send" && subjectChanged
                    ? "\n\nThe subject name changes, so Exchange Online's inbound connector (TlsSenderCertificateName) needs updating too: the Hybrid Configuration Wizard does both."
                    : "";
                if (!AppState.Confirm(
                        $"Point {fix.ConnectorType?.ToLowerInvariant()} connector '{fix.ConnectorName}' at:\n\n{cert.CommonName}\nIssued by {cert.IssuerShort}, valid to {cert.NotAfter:d}\nThumbprint {cert.Thumbprint}\n\n" +
                        $"TlsCertificateName becomes:\n{FixService.TlsNameFor(cert)}{exo}", "Update connector"))
                    return;
                await State.RunAsync("Updating the connector", () => State.Fixes!.UpdateConnectorAsync(fix, cert));
                break;
            }
        }

        if (!State.IsBusy) RunCommand.Execute(null);
    }
}
