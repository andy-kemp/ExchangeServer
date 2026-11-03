using System.Windows;
using System.Windows.Controls;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Views;

public partial class ImportView : UserControl
{
    public ImportView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImportViewModel vm) vm.Password = PasswordInput.Password;
    }
}
