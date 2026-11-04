using System.Windows;

namespace ExchangeCertManager;

/// <summary>The AKC splash. Also used, as WinImagePrep's updater window, while an update downloads and installs.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Services.UpdateService.CurrentText}";
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Turns the splash into the update window: "Updating", from and to versions, and a progress bar.</summary>
    public void ShowUpdate(string from, string to)
    {
        Title = "Updating Exchange Certificate Manager";
        UpdatingLabel.Visibility = Visibility.Visible;
        VersionText.Text = $"Version {from} \u2192 {to}";
        Tagline.Text = "The new version is checked against its published SHA-256 before it replaces this one. The app restarts when it's done.";
        Topmost = true;
    }

    /// <summary>Null shows the moving bar; a number (0-100) shows how far it's got.</summary>
    public void SetProgress(double? percent)
    {
        Progress.IsIndeterminate = percent is null;
        if (percent is { } p) Progress.Value = Math.Clamp(p, 0, 100);
    }
}
