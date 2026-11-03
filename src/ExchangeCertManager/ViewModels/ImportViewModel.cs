using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Services;
using Microsoft.Win32;

namespace ExchangeCertManager.ViewModels;

public sealed partial class ImportViewModel : PageViewModel
{
    public ImportViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Import PFX";
    public override string Subtitle => "Install a certificate you already have as a .pfx (exported from another server, or a wildcard you use elsewhere) on one or more servers, and assign services.";

    public ObservableCollection<SelectableServer> Targets { get; } = new();
    public ServiceSelection Services { get; } = new();

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ImportCommand))] private string _filePath = "";
    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _detailsState = "Info";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private bool _isValid;

    /// <summary>Set from the PasswordBox in the view.</summary>
    public string Password { private get; set; } = "";

    private byte[]? _bytes;

    public override void OnShown()
    {
        if (Targets.Count != State.Servers.Count)
        {
            Targets.Clear();
            foreach (var s in State.Servers) Targets.Add(new SelectableServer(s));
        }
    }

    partial void OnFilePathChanged(string value)
    {
        IsValid = false;
        _bytes = null;
        Details = "";
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a PFX file",
            Filter = "PFX / PKCS#12 (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) FilePath = dialog.FileName;
    }

    private bool HasFile() => File.Exists(FilePath);

    /// <summary>Opens the PFX locally first so you see what you're about to install, and catches a wrong password early.</summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Check()
    {
        IsValid = false;
        try
        {
            _bytes = File.ReadAllBytes(FilePath);
            var certs = new X509Certificate2Collection();
            certs.Import(_bytes, Password, X509KeyStorageFlags.EphemeralKeySet);
            var leaf = certs.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
                       ?? throw new InvalidOperationException("This file has no private key, so Exchange can't use it. Export it again with the private key.");

            var sans = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(e => e.EnumerateDnsNames()).ToList();
            var days = (int)Math.Floor((leaf.NotAfter - DateTime.Now).TotalDays);
            var inventory = State.Inventory.FirstOrDefault(g => g.Thumbprint.Equals(leaf.Thumbprint, StringComparison.OrdinalIgnoreCase));

            Details =
                $"Subject: {leaf.Subject}\n" +
                $"Names: {(sans.Count == 0 ? "(none)" : string.Join(", ", sans))}\n" +
                $"Issuer: {leaf.Issuer}\n" +
                $"Valid until: {leaf.NotAfter:dd MMM yyyy} ({(days < 0 ? $"expired {-days} days ago" : $"{days} days left")})\n" +
                $"Thumbprint: {leaf.Thumbprint}\n" +
                $"Chain certificates in file: {certs.Count - 1}" +
                (inventory is null ? "" : $"\nAlready installed on: {inventory.ServersText}");

            DetailsState = days < 0 ? "Error" : days < 30 ? "Warning" : "Ok";
            if (FriendlyName.Length == 0) FriendlyName = leaf.FriendlyName is { Length: > 0 } fn ? fn : $"Imported {leaf.GetNameInfo(X509NameType.DnsName, false)} {DateTime.Now:yyyy-MM}";
            IsValid = days >= 0;
            if (days < 0) Details += "\n\nThis certificate has expired, so it won't be imported.";
        }
        catch (CryptographicException)
        {
            DetailsState = "Error";
            Details = "Couldn't open the file. The password is probably wrong, or it isn't a PFX.";
        }
        catch (Exception ex)
        {
            DetailsState = "Error";
            Details = ex.Message;
        }
    }

    private bool CanImport() => IsValid && _bytes is not null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var servers = Targets.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        var services = Services.Selected();
        if (servers.Count == 0) { AppState.Inform("Pick at least one server."); return; }

        var smtp = services.Contains("SMTP") ? "\n\nAssigning SMTP replaces the default SMTP certificate." : "";
        if (!AppState.Confirm($"Import on {string.Join(", ", servers)} and assign {(services.Count == 0 ? "no services" : string.Join(", ", services))}?{smtp}"))
            return;

        var bytes = _bytes!;
        var password = CertificateService.ToSecure(Password);
        var ok = await State.RunAsync("Importing", async () =>
        {
            foreach (var server in servers)
            {
                State.SetBusyText($"Importing on {server}");
                var existing = await State.Certs!.GetCertificatesAsync(server);
                var thumb = await ImportOrReuse(server, bytes, password, existing);
                if (services.Count > 0)
                {
                    State.SetBusyText($"Assigning on {server}");
                    await State.Certs.EnableAsync(server, thumb, services);
                }
            }
            State.SetBusyText("Refreshing");
            await State.RefreshAsync();
        });

        if (ok)
        {
            AppState.Inform("Imported. Run the Health check to confirm the names cover your URLs.");
            Main.Navigate("inventory");
        }
    }

    private async Task<string> ImportOrReuse(string server, byte[] bytes, System.Security.SecureString password, List<Models.ServerCertificate> existing)
    {
        var certs = new X509Certificate2Collection();
        certs.Import(bytes, Password, X509KeyStorageFlags.EphemeralKeySet);
        var thumb = certs.Cast<X509Certificate2>().First(c => c.HasPrivateKey).Thumbprint;
        if (existing.Any(c => c.Thumbprint.Equals(thumb, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Info($"{server} already has {thumb}; skipping import");
            return thumb;
        }
        return await State.Certs!.ImportPfxAsync(server, bytes, password, FriendlyName.Trim());
    }
}
