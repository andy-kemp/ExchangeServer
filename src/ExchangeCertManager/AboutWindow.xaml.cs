using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using ExchangeCertManager.Services;

namespace ExchangeCertManager;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        txtVersion.Text = $"Version {UpdateService.CurrentText}";
        txtFolder.Text = AppPaths.ProgramData;
        var demo = Environment.GetCommandLineArgs().Any(a => a is "--demo" or "--screenshots");
        txtInstalled.Text = InstallService.IsRunningInstalledCopy || demo
            ? $"Installed in {AppPaths.InstallDirectory}."
            : $"Running from {Environment.ProcessPath}. File > Install on this Server puts a copy in Program Files.";
    }

    private void Website_Click(object sender, RoutedEventArgs e) => UpdateService.OpenUrl("https://www.andykemp.com/");
    private void Docs_Click(object sender, RoutedEventArgs e) => UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/");
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        UpdateService.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(AppPaths.ProgramData)) AppPaths.EnsureSecureDirectory(AppPaths.ProgramData);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.ProgramData}\"") { UseShellExecute = true });
    }
}
