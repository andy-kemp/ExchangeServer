using System.Windows;
using System.Windows.Controls;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Views;

public partial class ConnectView : UserControl
{
    public ConnectView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectViewModel vm) vm.Password = PasswordInput.Password;
    }
}
