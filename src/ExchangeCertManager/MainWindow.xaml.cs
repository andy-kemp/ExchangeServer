using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using ExchangeCertManager.Services;
using ExchangeCertManager.ViewModels;

namespace ExchangeCertManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Keep the log scrolled to the newest line. Deferred, because ScrollIntoView forces a layout pass,
        // and doing that inside a log call made during layout re-enters it.
        ((INotifyCollectionChanged)Log.Entries).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            });
        };

        Closed += (_, _) => AppState.Current.Session?.Dispose();
    }
}
