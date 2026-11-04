using System.Windows;
using System.Windows.Controls;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Views;

public partial class AcmeView : UserControl
{
    public AcmeView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AcmeViewModel old) old.CloudflareTokenSaved -= ClearToken;
            if (e.NewValue is AcmeViewModel vm) vm.CloudflareTokenSaved += ClearToken;
        };
    }

    // Once it's saved, the token doesn't stay on screen
    private void ClearToken(object? sender, EventArgs e) => CloudflareTokenBox.Clear();

    private void OnCloudflareTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AcmeViewModel vm) vm.CloudflareTokenInput = CloudflareTokenBox.Password;
    }

    private void OnTaskPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AcmeViewModel vm) vm.TaskPassword = TaskPasswordInput.Password;
    }
}
