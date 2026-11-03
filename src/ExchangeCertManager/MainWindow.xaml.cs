using System.Collections.Specialized;
using System.Windows;
using ExchangeCertManager.Services;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Keep the log scrolled to the newest line.
        ((INotifyCollectionChanged)Log.Entries).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };

        Closed += (_, _) => AppState.Current.Session?.Dispose();
    }
}
