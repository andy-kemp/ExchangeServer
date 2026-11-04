using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExchangeCertManager.Views;

/// <summary>Small themed dialogs built in code so they stay self-contained.</summary>
internal static class DialogKit
{
    public static Window NewWindow(string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ShowInTaskbar = false,
        };
        if (Application.Current.TryFindResource("AkcWindow") is Style style) w.Style = style;
        return w;
    }

    public static TextBlock Text(string text, bool bold = false, double size = 13) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        FontSize = size,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public static StackPanel Buttons(Window w, string okText, Func<bool>? onOk = null)
    {
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90 };
        if (Application.Current.TryFindResource("PrimaryButton") is Style primary) ok.Style = primary;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            if (onOk is null || onOk()) w.DialogResult = true;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(cancel);
        panel.Children.Add(ok);
        return panel;
    }
}

public static class PasswordPrompt
{
    /// <returns>The password, or null if cancelled.</returns>
    public static string? Ask(string message, bool confirm)
    {
        var w = DialogKit.NewWindow("Password", 420);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text(message));
        var first = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(first);
        PasswordBox? second = null;
        if (confirm)
        {
            root.Children.Add(DialogKit.Text("Confirm", size: 12));
            second = new PasswordBox();
            root.Children.Add(second);
        }
        var error = DialogKit.Text("", size: 12);
        error.Foreground = (Brush)Application.Current.FindResource("AkcDanger");
        root.Children.Add(error);
        root.Children.Add(DialogKit.Buttons(w, "OK", () =>
        {
            if (first.Password.Length < 8) { error.Text = "Use at least 8 characters."; return false; }
            if (second is not null && second.Password != first.Password) { error.Text = "The passwords don't match."; return false; }
            return true;
        }));
        w.Content = root;
        w.Loaded += (_, _) => first.Focus();
        return w.ShowDialog() == true ? first.Password : null;
    }
}

public static class DnsRecordsWindow
{
    /// <returns>True when the user says the records are in place.</returns>
    public static bool Show(IReadOnlyList<(string Name, string Value)> records)
    {
        var w = DialogKit.NewWindow("Add DNS records", 640);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text("Add these TXT records in public DNS", bold: true, size: 15));
        root.Children.Add(DialogKit.Text("Wait until they've propagated (check with nslookup -type=TXT name 8.8.8.8), then click Continue. You can remove them after the certificate is issued."));

        foreach (var (name, value) in records)
        {
            var card = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("AkcBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            AddRow(grid, 0, "Name", name);
            AddRow(grid, 1, "Value", value);
            card.Child = grid;
            root.Children.Add(card);
        }

        root.Children.Add(DialogKit.Buttons(w, "Continue"));
        w.Content = root;
        return w.ShowDialog() == true;
    }

    private static void AddRow(Grid grid, int row, string label, string value)
    {
        var l = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            Margin = new Thickness(0, 2, 6, 2),
            FontFamily = (FontFamily)Application.Current.FindResource("AkcMono"),
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 0, 2) };
        copy.Click += (_, _) => Clipboard.SetText(value);
        Grid.SetRow(l, row); Grid.SetRow(box, row); Grid.SetRow(copy, row);
        Grid.SetColumn(box, 1); Grid.SetColumn(copy, 2);
        grid.Children.Add(l); grid.Children.Add(box); grid.Children.Add(copy);
    }
}

public static class AuthRenewDialog
{
    /// <returns>The server to create it on and whether to make it active now, or null if cancelled.</returns>
    public static (string Server, bool Immediate)? Show(IReadOnlyList<string> servers, bool currentBroken, string? title = null)
    {
        var w = DialogKit.NewWindow(title ?? "Renew the Exchange Auth certificate", 560);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text(title ?? "Renew the Exchange Auth certificate", bold: true, size: 15));
        root.Children.Add(DialogKit.Text("A new self-signed Auth certificate is created on one server, copied to the others, and set as the Auth certificate. No services are assigned to it, and your SMTP certificate isn't touched."));

        root.Children.Add(DialogKit.Text("Create it on", bold: true, size: 12.5));
        var pick = new ComboBox { ItemsSource = servers, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(pick);

        root.Children.Add(DialogKit.Text("When it takes over", bold: true, size: 12.5));
        var staged = new RadioButton { IsChecked = !currentBroken, Margin = new Thickness(0, 0, 0, 6) };
        staged.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "In 49 hours (recommended). Microsoft's guidance: gives every server time to get it. It switches over on its own; nothing else to do." };
        var now = new RadioButton { IsChecked = currentBroken, Margin = new Thickness(0, 0, 0, 6) };
        now.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Now. For when the current one has expired or is missing. It's published straight away, the previous one is cleared and the OWA and ECP app pools are recycled (users may need to sign in to OWA again)." };
        root.Children.Add(staged);
        root.Children.Add(now);

        var note = DialogKit.Text("If you're hybrid, run the Hybrid Configuration Wizard again once the new certificate is active.", size: 12);
        note.Foreground = (Brush)Application.Current.FindResource("AkcMuted");
        root.Children.Add(note);

        root.Children.Add(DialogKit.Buttons(w, "Renew"));
        w.Content = root;
        if (w.ShowDialog() != true) return null;
        return ((string)pick.SelectedItem, now.IsChecked == true);
    }
}

public static class OptionsDialog
{
    /// <returns>True if saved.</returns>
    public static bool Show(ExchangeCertManager.ViewModels.UserSettings settings, Action checkNow)
    {
        var w = DialogKit.NewWindow("Options", 480);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text("Options", bold: true, size: 15));

        root.Children.Add(DialogKit.Text("Updates", bold: true, size: 12.5));
        var startup = new CheckBox
        {
            IsChecked = settings.CheckForUpdatesAtStartup,
            Margin = new Thickness(0, 0, 0, 6),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Check for a new version when the app opens" },
        };
        root.Children.Add(startup);
        var hint = DialogKit.Text($"You have version {ExchangeCertManager.Services.UpdateService.CurrentText}. New versions come from downloads.andykemp.com and are checked against their SHA-256 before they're installed. Nothing changes until you choose to update.", size: 12);
        hint.Foreground = (Brush)Application.Current.FindResource("AkcMuted");
        root.Children.Add(hint);
        var now = new Button { Content = "Check now", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
        now.Click += (_, _) =>
        {
            settings.CheckForUpdatesAtStartup = startup.IsChecked == true;
            settings.Save();
            w.DialogResult = true;
            checkNow();
        };
        root.Children.Add(now);

        root.Children.Add(DialogKit.Buttons(w, "Save"));
        w.Content = root;
        if (w.ShowDialog() != true) return false;
        settings.CheckForUpdatesAtStartup = startup.IsChecked == true;
        settings.Save();
        return true;
    }
}

public static class UpdateDialog
{
    /// <returns>True to update now.</returns>
    public static bool Ask(ExchangeCertManager.Services.UpdateInfo info)
    {
        var w = DialogKit.NewWindow("Update available", 500);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text($"Version {info.Version} is available", bold: true, size: 15));
        var when = string.IsNullOrEmpty(info.PublishedText) ? "" : $", released {info.PublishedText}";
        root.Children.Add(DialogKit.Text($"You have version {ExchangeCertManager.Services.UpdateService.CurrentText}{when}. Updating downloads the new version, checks its SHA-256, replaces this EXE in the same folder and restarts the app. The Let's Encrypt renewal task keeps working."));
        var link = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("What's new"));
        h.Click += (_, _) => ExchangeCertManager.Services.UpdateService.OpenUrl("https://docs.andykemp.com/exchange-cert-manager/whats-new/");
        link.Inlines.Add(h);
        root.Children.Add(link);
        var buttons = DialogKit.Buttons(w, "Update and restart");
        if (buttons.Children[0] is Button later) later.Content = "Later";
        root.Children.Add(buttons);
        w.Content = root;
        return w.ShowDialog() == true;
    }
}

public static class CreateGmsaDialog
{
    /// <returns>The options, or null if cancelled.</returns>
    public static ExchangeCertManager.Services.GmsaSetupOptions? Show(string suggestedName)
    {
        var w = DialogKit.NewWindow("Create a gMSA for the renewal task", 560);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogKit.Text("Create a gMSA for the renewal task", bold: true, size: 15));
        root.Children.Add(DialogKit.Text("A group Managed Service Account has no password for anyone to know or rotate: AD changes it and Task Scheduler fetches it. This creates one (or reuses it if it exists), lets this computer use it (the renewal task runs here, whether that's an Exchange server or a management machine), makes it a local administrator here, and gives it only the Exchange rights renewal needs. You need rights to create accounts in AD (for example Domain Admins) and Organization Management."));

        root.Children.Add(DialogKit.Text("Name", bold: true, size: 12.5));
        var name = new TextBox { Text = suggestedName, Margin = new Thickness(0, 0, 0, 4) };
        root.Children.Add(name);
        var nameHint = DialogKit.Text("Up to 15 characters: AD cuts a gMSA's account name at 15.", size: 12);
        nameHint.Foreground = (Brush)Application.Current.FindResource("AkcMuted");
        root.Children.Add(nameHint);

        root.Children.Add(DialogKit.Text("Which servers can use it", bold: true, size: 12.5));
        var thisServer = new RadioButton { IsChecked = true, Margin = new Thickness(0, 0, 0, 4), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Only this computer, {Environment.MachineName} (works straight away)" } };
        var allServers = new RadioButton { Margin = new Thickness(0, 0, 0, 10), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Every Exchange server (the Exchange Servers group; a server added to it needs a restart before it can use the gMSA)" } };
        root.Children.Add(thisServer);
        root.Children.Add(allServers);

        var roleGroup = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 0, 6), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Create the '{ExchangeCertManager.Services.GmsaSetupService.RoleGroupName}' Exchange role group (Exchange Server Certificates and View-Only Configuration) and add the gMSA to it" } };
        root.Children.Add(roleGroup);
        var backdate = new CheckBox { Margin = new Thickness(0, 0, 0, 4), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "If the forest has no KDS root key yet, back-date it so it works now (labs only: in production, wait the 10 hours Microsoft recommends)" } };
        root.Children.Add(backdate);

        var error = DialogKit.Text("", size: 12);
        error.Foreground = (Brush)Application.Current.FindResource("AkcDanger");
        root.Children.Add(error);

        root.Children.Add(DialogKit.Buttons(w, "Create", () =>
        {
            var problem = ExchangeCertManager.Services.GmsaSetupService.ValidateName(name.Text.Trim().TrimEnd('$'));
            error.Text = problem ?? "";
            return problem is null;
        }));
        w.Content = root;
        if (w.ShowDialog() != true) return null;
        return new ExchangeCertManager.Services.GmsaSetupOptions(name.Text.Trim().TrimEnd('$'), allServers.IsChecked == true, backdate.IsChecked == true, roleGroup.IsChecked == true);
    }
}
