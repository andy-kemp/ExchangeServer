using System.Windows;
using System.Windows.Controls;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Views;

public partial class AlertsView : UserControl
{
    public AlertsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AlertsViewModel old) old.Smtp2GoKeySaved -= ClearKey;
            if (e.NewValue is AlertsViewModel vm) vm.Smtp2GoKeySaved += ClearKey;
        };
    }

    // Once it's saved, the key doesn't stay on screen
    private void ClearKey(object? sender, EventArgs e) => Smtp2GoKeyBox.Clear();

    private void OnSmtp2GoKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlertsViewModel vm) vm.Smtp2GoKeyInput = Smtp2GoKeyBox.Password;
    }
}
