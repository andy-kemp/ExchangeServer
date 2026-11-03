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
