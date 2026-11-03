using System.Reflection;
using System.Windows;

namespace ExchangeCertManager;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
