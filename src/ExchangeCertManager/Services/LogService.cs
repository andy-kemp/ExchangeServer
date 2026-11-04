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
/// Writes to the in-app log panel and to C:\ProgramData\ExchangeCertManager\Logs\yyyy-MM-dd.log.
/// Safe to call from any thread.
/// </summary>
public static class Log
{
    public static string LogDirectory { get; } = Path.Combine(AppPaths.ProgramData, "Logs");
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
                // The parent holds the ACME key and Cloudflare token, so it's locked down before anything is created in it
                if (!Directory.Exists(LogDirectory)) AppPaths.EnsureSecureDirectory(AppPaths.ProgramData);
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
    /// <summary>
    /// Everything the app keeps: logs, the Let's Encrypt account key, saved renewals and the Cloudflare token.
    /// Readable only by Administrators and SYSTEM. (Before 1.5.0 the logs were in C:\ExchangeCertManager\Logs.)
    /// </summary>
    public static string ProgramData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExchangeCertManager");

    /// <summary>Where "Install" puts the EXE, and where the renewal task runs it from.</summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Andy Kemp Consulting", "Exchange Certificate Manager");

    public static string InstalledExe { get; } = Path.Combine(InstallDirectory, "ExchangeCertManager.exe");

    /// <summary>Per-user UI settings.</summary>
    public static string UserSettings { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExchangeCertManager", "settings.json");

    public static bool IsInstalledCopy(string? exe) =>
        exe is not null && string.Equals(Path.GetFullPath(exe), InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a folder readable only by Administrators and SYSTEM (no-op if it exists).</summary>
    public static void EnsureSecureDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists) return;
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.Principal.WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(sid, null), System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        }
        dir.Create(security);
    }
}
