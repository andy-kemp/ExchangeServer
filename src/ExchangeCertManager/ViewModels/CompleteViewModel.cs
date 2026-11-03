using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using Microsoft.Win32;

namespace ExchangeCertManager.ViewModels;

public sealed partial class CompleteViewModel : PageViewModel
{
    public CompleteViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Complete a request";
    public override string Subtitle => "Import the CA's response on the server that holds the pending request, then assign services and copy it to your other servers in one go.";

    public ObservableCollection<ServerCertificate> Pending { get; } = new();
    public ObservableCollection<SelectableServer> OtherServers { get; } = new();
    public ServiceSelection Services { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    private ServerCertificate? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    private string _filePath = "";

    [ObservableProperty] private bool _assignAfter = true;
    [ObservableProperty] private bool _deployAfter = true;

    public bool HasPending => Pending.Count > 0;

    public override void OnShown()
    {
        var keep = Selected;
        Pending.Clear();
        foreach (var p in State.Inventory.SelectMany(g => g.Installs).Where(i => i.IsPending).OrderBy(i => i.Server))
            Pending.Add(p);
        Selected = Pending.FirstOrDefault(p => p.Thumbprint == keep?.Thumbprint) ?? Pending.FirstOrDefault();
        OnPropertyChanged(nameof(HasPending));
    }

    partial void OnSelectedChanged(ServerCertificate? value)
    {
        OtherServers.Clear();
        if (value is null) return;
        foreach (var s in State.Servers.Where(s => !s.Name.Equals(value.Server, StringComparison.OrdinalIgnoreCase)))
            OtherServers.Add(new SelectableServer(s));
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the certificate from your CA",
            Filter = "Certificate files (*.cer;*.crt;*.p7b;*.pem)|*.cer;*.crt;*.p7b;*.pem|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) FilePath = dialog.FileName;
    }

    private bool CanComplete() => Selected is not null && File.Exists(FilePath);

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteAsync()
    {
        var pending = Selected!;
        var services = AssignAfter ? Services.Selected() : new List<string>();
        var targets = DeployAfter ? OtherServers.Where(s => s.IsSelected).Select(s => s.Name).ToList() : new List<string>();

        var ok = await State.RunAsync($"Completing on {pending.Server}", async () =>
        {
            var bytes = await File.ReadAllBytesAsync(FilePath);
            var thumb = await State.Certs!.CompleteRequestAsync(pending.Server, bytes);

            if (services.Count > 0)
            {
                State.SetBusyText($"Assigning {string.Join(", ", services)} on {pending.Server}");
                await State.Certs.EnableAsync(pending.Server, thumb, services);
            }

            if (targets.Count > 0)
            {
                var progress = new Progress<string>(State.SetBusyText);
                await State.Certs.DeployAsync(pending.Server, thumb, targets, services, progress);
            }

            State.SetBusyText("Refreshing");
            await State.RefreshAsync();
            Log.Success($"Done. New certificate {thumb}");
        });

        if (ok)
        {
            FilePath = "";
            AppState.Inform("Certificate installed. Run the Health page to confirm every URL and connector is covered.");
            Main.Navigate("inventory");
        }
    }
}
