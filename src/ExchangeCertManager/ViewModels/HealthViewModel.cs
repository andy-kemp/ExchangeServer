using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.ViewModels;

public sealed partial class HealthViewModel : PageViewModel
{
    public HealthViewModel(MainViewModel main) : base(main) { }

    public override string Title => "Health check";
    public override string Subtitle => "Expiry, the Exchange Auth certificate, SAN coverage of your configured URLs, connectors pinned to old certificates, and clean-up candidates.";

    public ObservableCollection<HealthItem> Items { get; } = new();

    [ObservableProperty] private string _summary = "Run the checks to see how your certificates look.";
    [ObservableProperty] private DateTime? _lastRun;

    public override void OnShown()
    {
        if (LastRun is null && State.IsConnected && !State.IsBusy) RunCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        await State.RunAsync("Running health checks", async () =>
        {
            var progress = new Progress<string>(State.SetBusyText);
            var items = await State.Health!.RunAsync(State.Servers.ToList(), progress);
            Items.Clear();
            foreach (var i in items) Items.Add(i);

            var errors = items.Count(i => i.Severity == HealthSeverity.Error);
            var warnings = items.Count(i => i.Severity == HealthSeverity.Warning);
            Summary = errors + warnings == 0
                ? "All good."
                : $"{errors} problem(s) to fix and {warnings} warning(s).";
            LastRun = DateTime.Now;
        });
    }
}
