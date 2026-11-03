using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using ExchangeCertManager.Views;
using Microsoft.Win32;

namespace ExchangeCertManager.ViewModels;

public sealed partial class InventoryViewModel : PageViewModel
{
    public InventoryViewModel(MainViewModel main) : base(main)
    {
        Items = new ListCollectionView(State.Inventory) { Filter = Include };
    }

    public override string Title => "Certificates";
    public override string Subtitle => "Every certificate across your Exchange servers. Select one to assign services, renew, export, deploy or remove it.";

    public ICollectionView Items { get; }

    [ObservableProperty] private bool _showSelfSigned;
    [ObservableProperty] private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AssignCommand), nameof(RenewCommand), nameof(ExportCommand),
        nameof(DeployCommand), nameof(RemoveCommand), nameof(CopyThumbprintCommand))]
    private CertificateGroup? _selected;

    public bool HasSelection => Selected is not null;
    public ServiceSelection Services { get; } = new();
    public ObservableCollection<SelectableServer> AssignServers { get; } = new();

    public string Summary
    {
        get
        {
            var inUse = State.Inventory.Where(g => g.HasServices && !g.IsSelfSigned).ToList();
            var soon = inUse.Count(g => g.DaysLeft is < 60);
            return $"{State.Inventory.Count} certificates · {inUse.Count} CA-issued in use · {soon} expiring within 60 days";
        }
    }

    public override void OnShown()
    {
        Items.Refresh();
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnShowSelfSignedChanged(bool value) => Items.Refresh();
    partial void OnSearchChanged(string value) => Items.Refresh();

    private bool Include(object o)
    {
        if (o is not CertificateGroup g) return false;
        if (!ShowSelfSigned && g.IsSelfSigned && !g.IsAuthCertificate) return false;
        if (Search.Length == 0) return true;
        return g.Subject.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || g.DomainsText.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || g.Thumbprint.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || g.FriendlyName.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedChanged(CertificateGroup? value)
    {
        AssignServers.Clear();
        if (value is null) return;
        foreach (var install in value.Installs)
        {
            var server = State.Servers.FirstOrDefault(s => s.Name.Equals(install.Server, StringComparison.OrdinalIgnoreCase));
            if (server is not null) AssignServers.Add(new SelectableServer(server));
        }
        Services.Set(value.Installs.SelectMany(i => i.Services.Split(',', StringSplitOptions.TrimEntries)));
        if (!value.HasServices) Services.Iis = true;
    }

    [RelayCommand]
    private Task RefreshAsync() => State.RunAsync("Refreshing certificates", async () =>
    {
        var keep = Selected?.Thumbprint;
        await State.RefreshAsync();
        Items.Refresh();
        OnPropertyChanged(nameof(Summary));
        Selected = State.Inventory.FirstOrDefault(g => g.Thumbprint == keep);
    });

    private bool CanUse() => Selected is not null && !Selected.IsPending;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task AssignAsync()
    {
        var g = Selected!;
        var services = Services.Selected();
        var servers = AssignServers.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        if (services.Count == 0 || servers.Count == 0)
        {
            AppState.Inform("Pick at least one service and one server.");
            return;
        }

        var smtpNote = services.Contains("SMTP")
            ? "\n\nThis replaces the default SMTP certificate. If you're hybrid, check the connectors' TlsCertificateName afterwards (the Health page does this)."
            : "";
        if (!AppState.Confirm($"Assign {string.Join(", ", services)} to {g.CommonName} on {string.Join(", ", servers)}?{smtpNote}")) return;

        await State.RunAsync("Assigning services", async () =>
        {
            foreach (var s in servers)
            {
                State.SetBusyText($"Assigning on {s}");
                await State.Certs!.EnableAsync(s, g.Thumbprint, services);
            }
            await RefreshAfterChange(g.Thumbprint);
        });
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Renew()
    {
        Main.Request.PrefillFrom(Selected!);
        Main.Navigate("request");
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Deploy()
    {
        Main.Deploy.Prefill(Selected!);
        Main.Navigate("deploy");
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ExportAsync()
    {
        var g = Selected!;
        if (!g.PrivateKeyExportable)
        {
            AppState.Inform("This certificate's private key isn't marked exportable, so Exchange can't export it.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export certificate as PFX",
            Filter = "PFX file (*.pfx)|*.pfx",
            FileName = $"{g.CommonName.Replace("*", "wildcard")}-{DateTime.Now:yyyyMMdd}.pfx",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;

        var password = PasswordPrompt.Ask("Set a password to protect the exported PFX.", confirm: true);
        if (password is null) return;

        await State.RunAsync("Exporting", async () =>
        {
            var server = g.Installs.First(i => i.PrivateKeyExportable).Server;
            var bytes = await State.Certs!.ExportPfxAsync(server, g.Thumbprint, CertificateService.ToSecure(password));
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Log.Success($"Exported {g.CommonName} to {dialog.FileName}");
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var g = Selected!;
        var servers = AssignServers.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        if (servers.Count == 0) return;

        if (g.IsAuthCertificate)
        {
            AppState.Inform("This is the Exchange Auth certificate. Removing it breaks OAuth and hybrid, so it's blocked here.");
            return;
        }

        var inUse = g.Installs.Where(i => servers.Contains(i.Server) && i.HasServices).Select(i => $"{i.Server} ({i.Services})").ToList();
        var warning = inUse.Count > 0 ? $"\n\nIt's still assigned on {string.Join(", ", inUse)}. Assign the replacement first." : "";
        if (!AppState.Confirm($"Remove {g.CommonName} ({g.Thumbprint}) from {string.Join(", ", servers)}?{warning}\n\nThis can't be undone.", "Remove certificate"))
            return;

        await State.RunAsync("Removing", async () =>
        {
            foreach (var s in servers) await State.Certs!.RemoveAsync(s, g.Thumbprint);
            await RefreshAfterChange(null);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyThumbprint() => Clipboard.SetText(Selected!.Thumbprint);

    private async Task RefreshAfterChange(string? thumbprint)
    {
        State.SetBusyText("Refreshing");
        await State.RefreshAsync();
        Items.Refresh();
        OnPropertyChanged(nameof(Summary));
        Selected = thumbprint is null ? null : State.Inventory.FirstOrDefault(x => x.Thumbprint == thumbprint);
    }
}
