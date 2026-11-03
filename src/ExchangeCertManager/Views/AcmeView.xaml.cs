using System.Windows;
using System.Windows.Controls;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager.Views;

public partial class AcmeView : UserControl
{
    public AcmeView() => InitializeComponent();

    private void OnTaskPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AcmeViewModel vm) vm.TaskPassword = TaskPasswordInput.Password;
    }
}
