using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ExchangeCertManager.Services;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
}

/// <summary>
/// Writes to the in-app log panel and to C:\ExchangeCertManager\Logs\yyyy-MM-dd.log.
/// Safe to call from any thread.
/// </summary>
public static class Log
{
    public static string LogDirectory { get; } = Path.Combine(AppPaths.DataRoot, "Logs");
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    private static readonly object FileLock = new();

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Success(string message) => Write(LogLevel.Success, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }

        var app = Application.Current;
        if (app?.Dispatcher is null) return; // headless renewal mode
        if (app.Dispatcher.CheckAccess()) Add(entry);
        else app.Dispatcher.BeginInvoke(() => Add(entry));
    }

    private static void Add(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > 2000) Entries.RemoveAt(0);
    }
}

public static class AppPaths
{
    /// <summary>Logs live here so they're easy to find on a server (matches WinImagePrep's C:\WinImagePrep\Logs).</summary>
    public static string DataRoot { get; } = Path.Combine(
        Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "ExchangeCertManager");

    /// <summary>Machine-wide state (ACME account, renewal definitions). Locked down to Administrators and SYSTEM.</summary>
    public static string ProgramData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExchangeCertManager");

    /// <summary>Per-user UI settings.</summary>
    public static string UserSettings { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExchangeCertManager", "settings.json");
}
