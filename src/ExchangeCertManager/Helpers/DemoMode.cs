using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExchangeCertManager.Models;
using ExchangeCertManager.Services;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Helpers;

/// <summary>
/// --demo fills the app with a sample Contoso organisation so it can be shown without Exchange.
/// --screenshots &lt;folder&gt; does the same, saves a PNG of the splash and every page, then exits
/// (used by the build to produce README screenshots).
/// </summary>
public static class DemoMode
{
    public const string DemoArgument = "--demo";
    public const string ScreenshotArgument = "--screenshots";

    public static void Seed(MainViewModel main)
    {
        var state = AppState.Current;
        Log.Entries.Clear(); // start the log panel clean: only the sample organisation's lines
        state.EnterDemo("ex01.ad.contoso.com · CONTOSO\\andy");

        var ex01 = new ExchangeServerInfo { Name = "EX01", Fqdn = "ex01.ad.contoso.com", Version = "Version 15.2 (Build 2562.17)", Roles = "Mailbox" };
        var ex02 = new ExchangeServerInfo { Name = "EX02", Fqdn = "ex02.ad.contoso.com", Version = "Version 15.2 (Build 2562.17)", Roles = "Mailbox" };
        state.Servers.Add(ex01);
        state.Servers.Add(ex02);

        var now = DateTime.Now;
        ServerCertificate Cert(string server, string thumb, string cn, string issuer, string friendly, int days, string services,
            string[] domains, bool selfSigned = false, string status = "Valid") => new()
        {
            Server = server,
            Thumbprint = thumb,
            Subject = cn.StartsWith("CN=") ? cn : $"CN={cn}, O=Contoso Ltd, L=Edinburgh, C=GB",
            Issuer = issuer,
            FriendlyName = friendly,
            NotBefore = now.AddDays(days - 365),
            NotAfter = now.AddDays(days),
            Domains = domains.ToList(),
            Services = services,
            Status = status,
            IsSelfSigned = selfSigned,
            HasPrivateKey = true,
            PrivateKeyExportable = !selfSigned,
        };

        string[] mailNames = { "mail.contoso.com", "autodiscover.contoso.com", "owa.contoso.com", "ex01.ad.contoso.com", "ex02.ad.contoso.com" };
        var digicert = "CN=DigiCert Global G2 TLS RSA SHA256 2020 CA1, O=DigiCert Inc, C=US";
        var groups = new List<CertificateGroup>
        {
            new()
            {
                Thumbprint = "7A3F1C9E42B8D05E6A1F3C7B9D2E4F6A8B0C1D2E",
                Installs = new()
                {
                    Cert("EX01", "7A3F1C9E42B8D05E6A1F3C7B9D2E4F6A8B0C1D2E", "mail.contoso.com", digicert, "Exchange mail.contoso.com 2026", 214, "IMAP, POP, IIS, SMTP", mailNames),
                    Cert("EX02", "7A3F1C9E42B8D05E6A1F3C7B9D2E4F6A8B0C1D2E", "mail.contoso.com", digicert, "Exchange mail.contoso.com 2026", 214, "IIS, SMTP", mailNames),
                },
            },
            new()
            {
                Thumbprint = "1D2C3B4A59687F6E5D4C3B2A1908F7E6D5C4B3A2",
                Installs = new()
                {
                    Cert("EX01", "1D2C3B4A59687F6E5D4C3B2A1908F7E6D5C4B3A2", "*.contoso.com", "CN=R11, O=Let's Encrypt, C=US", "Let's Encrypt *.contoso.com", 41, "None",
                        new[] { "*.contoso.com", "contoso.com" }),
                },
            },
            new()
            {
                Thumbprint = "9F8E7D6C5B4A39281706F5E4D3C2B1A098765432",
                IsAuthCertificate = true,
                Installs = new()
                {
                    Cert("EX01", "9F8E7D6C5B4A39281706F5E4D3C2B1A098765432", "CN=Microsoft Exchange Server Auth Certificate", "CN=Microsoft Exchange Server Auth Certificate",
                        "Microsoft Exchange Server Auth Certificate", 41, "SMTP", Array.Empty<string>(), selfSigned: true),
                    Cert("EX02", "9F8E7D6C5B4A39281706F5E4D3C2B1A098765432", "CN=Microsoft Exchange Server Auth Certificate", "CN=Microsoft Exchange Server Auth Certificate",
                        "Microsoft Exchange Server Auth Certificate", 41, "SMTP", Array.Empty<string>(), selfSigned: true),
                },
            },
            new()
            {
                Thumbprint = "0B1C2D3E4F5061728394A5B6C7D8E9F001122334",
                Installs = new()
                {
                    Cert("EX01", "0B1C2D3E4F5061728394A5B6C7D8E9F001122334", "mail.contoso.com", digicert, "Exchange mail.contoso.com 2025", -18, "None", mailNames),
                },
            },
            new()
            {
                Thumbprint = "55AA66BB77CC88DD99EE00FF11AA22BB33CC44DD",
                Installs = new()
                {
                    Cert("EX01", "55AA66BB77CC88DD99EE00FF11AA22BB33CC44DD", "mail.contoso.com", "CN=mail.contoso.com", "Exchange mail.contoso.com 2027 (renewal)", 0, "None",
                        mailNames, status: "PendingRequest"),
                },
            },
        };
        foreach (var g in groups) state.Inventory.Add(g);

        main.Health.Items.Clear();
        foreach (var h in new[]
        {
            new HealthItem(HealthSeverity.Error, "Client access", "EX02", "mail.contoso.com doesn't cover all configured URLs",
                "Not on the certificate: legacy.contoso.com. Either add these names at renewal or change the virtual directory URLs."),
            new HealthItem(HealthSeverity.Error, "Mail flow", "EX01", "Send connector 'Outbound to Office 365 - 3f8a' points at a certificate that isn't installed",
                "TlsCertificateName is <I>CN=DigiCert Global G2 TLS RSA SHA256 2020 CA1, O=DigiCert Inc, C=US<S>CN=mail.contoso.com, O=Contoso Limited. Point it at the current certificate. Hybrid connectors are the usual culprit.")
                { Fix = new HealthFix(HealthFixKind.UpdateConnectorCertificate, "Update connector…") },
            new HealthItem(HealthSeverity.Warning, "Auth certificate", "", "The Exchange Auth certificate expires in 41 days",
                "Renew it before it expires; staged renewal takes 49 hours to go live. Re-run the Hybrid Configuration Wizard afterwards if you're hybrid. No next certificate is staged.")
                { Fix = new HealthFix(HealthFixKind.RenewAuthCertificate, "Renew…") },
            new HealthItem(HealthSeverity.Warning, "Consistency", "EX02", "*.contoso.com isn't on every server",
                "Behind a load balancer, clients hitting these servers get a different certificate. Use Deploy to copy it across."),
            new HealthItem(HealthSeverity.Info, "Clean-up", "EX01", "Expired, unused: mail.contoso.com",
                "Thumbprint 0B1C2D3E4F5061728394A5B6C7D8E9F001122334. Safe to remove once you've checked nothing references it."),
            new HealthItem(HealthSeverity.Info, "Clean-up", "EX01", "1 pending request(s)",
                "Complete them with the CA response, or remove them if they were abandoned."),
            new HealthItem(HealthSeverity.Ok, "Client access", "EX01", "Certificate covers every configured URL",
                "mail.contoso.com covers autodiscover.contoso.com, ex01.ad.contoso.com, mail.contoso.com, owa.contoso.com."),
        }) main.Health.Items.Add(h);
        main.Health.Summary = "2 problem(s) to fix and 2 warning(s).";
        main.Health.LastRun = now;

        var vd = new List<VdirEntry>();
        foreach (var (srv, fqdn, ext) in new[] { ("EX01", "ex01.ad.contoso.com", "mail.contoso.com"), ("EX02", "ex02.ad.contoso.com", "") })
        {
            foreach (var k in VirtualDirectoryService.UrlKinds)
                vd.Add(new VdirEntry
                {
                    Server = srv, Kind = k.Key, Label = k.Label, Identity = $"{srv}\\{k.Path.Trim('/').Split('/')[0]} (Default Web Site)",
                    Internal = $"https://{fqdn}{k.Path}", External = ext.Length == 0 ? "" : $"https://{ext}{k.Path}",
                });
            vd.Add(new VdirEntry { Server = srv, Kind = VirtualDirectoryService.OutlookAnywhere, Label = "Outlook Anywhere (hostnames)", Identity = $"{srv}\\Rpc (Default Web Site)", Internal = fqdn, External = ext });
            vd.Add(new VdirEntry { Server = srv, Kind = VirtualDirectoryService.Autodiscover, Label = "Autodiscover (internal SCP)", Identity = srv, Internal = $"https://{fqdn}/Autodiscover/Autodiscover.xml" });
        }
        main.Urls.OnShown();
        main.Urls.Load(vd);
        main.Urls.PreviewCommand.Execute(null);

        main.Request.Server = ex01;
        main.Request.CommonName = "mail.contoso.com";
        main.Request.DomainsText = string.Join(Environment.NewLine, mailNames.Skip(1));
        main.Request.FriendlyName = "Exchange mail.contoso.com 2027";
        main.Request.Organisation = "Contoso Ltd";
        main.Request.City = "Edinburgh";
        main.Request.Country = "GB";
        main.Request.Csr = "-----BEGIN NEW CERTIFICATE REQUEST-----\nMIIDBjCCAe4CAQAwZDELMAkGA1UEBhMCR0IxEjAQBgNVBAcTCUVkaW5idXJnaDEU\nMBIGA1UEChMLQ29udG9zbyBMdGQxGTAXBgNVBAMTEG1haWwuY29udG9zby5jb20w\nggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQC7vbqajDw4o6gJy8UtmIbk\n...\n-----END NEW CERTIFICATE REQUEST-----";

        main.Acme.Email = "andy@contoso.com";
        main.Acme.DomainsText = "mail.contoso.com\nautodiscover.contoso.com\nowa.contoso.com";

        foreach (var (level, msg) in new[]
        {
            (LogLevel.Info, "Exchange Certificate Manager started on EX01"),
            (LogLevel.Success, "Connected to ex01.ad.contoso.com as CONTOSO\\andy"),
            (LogLevel.Info, "> Get-ExchangeServer"),
            (LogLevel.Info, "> Get-ExchangeCertificate -Server 'EX01'"),
            (LogLevel.Info, "> Get-ExchangeCertificate -Server 'EX02'"),
            (LogLevel.Info, "5 certificates on 2 server(s)"),
            (LogLevel.Warning, "Get-MapiVirtualDirectory on EX02: The operation couldn't be performed because object 'EX02\\mapi' couldn't be found."),
        }) Log.Write(level, msg);
    }

    /// <summary>Seeds, captures every page to PNG, then shuts the app down.</summary>
    public static async Task CaptureAsync(Window window, MainViewModel main, string folder)
    {
        Directory.CreateDirectory(folder);
        var pages = new (string Key, string File, Action? Prepare)[]
        {
            ("inventory", "02-certificates.png", () => main.Inventory.Selected = AppState.Current.Inventory.FirstOrDefault()),
            ("health", "03-health.png", null),
            ("urls", "09-virtual-directories.png", null),
            ("urls", "10-virtual-directories-preview.png", () => (window.FindName("PageScroll") as System.Windows.Controls.ScrollViewer)?.ScrollToBottom()),
            ("request", "04-request.png", null),
            ("complete", "05-complete.png", null),
            ("import", "06-import.png", () =>
            {
                main.Import.FilePath = @"C:\Certs\wildcard-contoso-2026.pfx";
                main.Import.Details = "Subject: CN=*.contoso.com\nNames: *.contoso.com, contoso.com\nIssuer: CN=Sectigo RSA Domain Validation Secure Server CA\nValid until: 14 Aug 2027 (284 days left)\nThumbprint: 3E4F5A6B7C8D9E0F1A2B3C4D5E6F7A8B9C0D1E2F\nChain certificates in file: 2";
                main.Import.DetailsState = "Ok";
                main.Import.FriendlyName = "Sectigo *.contoso.com 2026";
            }),
            ("deploy", "07-deploy.png", null),
            ("acme", "08-lets-encrypt.png", () =>
            {
                main.Acme.Renewals.Add(new AcmeRenewal
                {
                    Domains = new() { "mail.contoso.com", "autodiscover.contoso.com" },
                    Servers = new() { "EX01", "EX02" },
                    NotAfter = DateTime.Now.AddDays(71),
                    LastResult = "Issued 4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F",
                });
                main.Acme.Renewals.Add(new AcmeRenewal
                {
                    Domains = new() { "*.contoso.com", "contoso.com" },
                    Servers = new() { "EX01", "EX02" },
                    Challenge = AcmeChallengeType.Dns01Cloudflare,
                    NotAfter = DateTime.Now.AddDays(58),
                    LastResult = "Issued 9A8B7C6D5E4F30211A2B3C4D5E6F708192A3B4C",
                });
                main.Acme.Challenge = AcmeChallengeType.Dns01Cloudflare;
                main.Acme.CloudflareStatus = "Token saved. It can edit: contoso.com.";
                main.Acme.TaskStatus = "The daily renewal task is registered. It renews anything within 30 days of expiry.";
            }),
            ("connect", "01-connect.png", null),
        };

        foreach (var (key, file, prepare) in pages)
        {
            main.Navigate(key);
            (window.FindName("PageScroll") as System.Windows.Controls.ScrollViewer)?.ScrollToTop();
            main.ShowLog = key == "inventory"; // the log panel once is enough; more of each page otherwise
            prepare?.Invoke();
            await Settle(window);
            Save(window, Path.Combine(folder, file));
        }

        // Help > About
        var about = new AboutWindow { Owner = window, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        about.Show();
        await Settle(about);
        Save(about, Path.Combine(folder, "11-about.png"));
        about.Close();
    }

    public static async Task CaptureSplashAsync(SplashWindow splash, string folder)
    {
        Directory.CreateDirectory(folder);
        await Settle(splash);
        Save(splash, Path.Combine(folder, "00-splash.png"));
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(600);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        // Render the window itself (its client area), so content margins and the splash's shadow aren't cropped.
        FrameworkElement root = window;
        var dpi = VisualTreeHelper.GetDpi(root);
        var size = window.Content is FrameworkElement content && window.WindowStyle != WindowStyle.None
            ? new Size(content.ActualWidth, content.ActualHeight)
            : new Size(window.ActualWidth, window.ActualHeight);
        if (window.WindowStyle != WindowStyle.None && window.Content is FrameworkElement c) root = c;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) }, null, new Rect(size));
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"Saved {path}"); // not to the log panel, which is in the next capture
    }
}
