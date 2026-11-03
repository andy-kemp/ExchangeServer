using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;

namespace ExchangeCertManager.ViewModels;

public sealed partial class DeployViewModel : PageViewModel
{
    public DeployViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Deploy to servers";
    public override string Subtitle => "Exports the certificate once, imports it on every server that doesn't have it and assigns the same services everywhere.";

    public ObservableCollection<CertificateGroup> Certificates { get; } = new();
    public ObservableCollection<SelectableServer> Targets { get; } = new();
    public ServiceSelection Services { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private CertificateGroup? _selected;

    public string SourceText => Selected is null ? ""
        : Selected.Installs.FirstOrDefault(i => i.PrivateKeyExportable) is { } src
            ? $"Source: {src.Server} (currently on {Selected.ServersText})"
            : "None of the servers holding this certificate can export its private key.";

    public override void OnShown()
    {
        var keep = Selected?.Thumbprint;
        Certificates.Clear();
        foreach (var g in State.Inventory.Where(g => !g.IsPending && (!g.IsSelfSigned || g.IsAuthCertificate)))
            Certificates.Add(g);
        Selected = Certificates.FirstOrDefault(c => c.Thumbprint == keep) ?? Certificates.FirstOrDefault();
    }

    public void Prefill(CertificateGroup g)
    {
        OnShown();
        Selected = Certificates.FirstOrDefault(c => c.Thumbprint == g.Thumbprint);
    }

    partial void OnSelectedChanged(CertificateGroup? value)
    {
        Targets.Clear();
        if (value is null) return;
        foreach (var s in State.Servers) Targets.Add(new SelectableServer(s, selected: true));

        if (value.IsAuthCertificate) Services.Set(Array.Empty<string>()); // Auth cert just needs to be present
        else Services.Set(value.Installs.SelectMany(i => i.Services.Split(',', StringSplitOptions.TrimEntries)));
    }

    private bool CanRun() => Selected?.Installs.Any(i => i.PrivateKeyExportable) == true;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var g = Selected!;
        var source = g.Installs.First(i => i.PrivateKeyExportable).Server;
        var targets = Targets.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        var services = Services.Selected();
        if (targets.Count == 0) { AppState.Inform("Pick at least one server."); return; }

        var newOnes = targets.Where(t => !g.Installs.Any(i => i.Server.Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var summary =
            $"Certificate: {g.CommonName} ({g.Thumbprint})\n" +
            $"Export from: {source}\n" +
            $"Import on: {(newOnes.Count == 0 ? "none (already present everywhere)" : string.Join(", ", newOnes))}\n" +
            $"Assign: {(services.Count == 0 ? "no services" : string.Join(", ", services))} on {string.Join(", ", targets)}\n\nGo ahead?";
        if (!AppState.Confirm(summary, "Deploy certificate")) return;

        var ok = await State.RunAsync("Deploying", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            await State.Certs!.DeployAsync(source, g.Thumbprint, targets, services, progress);
            State.SetBusyText("Refreshing");
            await State.RefreshAsync();
        });

        if (ok)
        {
            Log.Success($"Deployed {g.CommonName} to {string.Join(", ", targets)}");
            OnShown();
        }
    }
}
