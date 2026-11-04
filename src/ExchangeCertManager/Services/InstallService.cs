using System.Diagnostics;
using System.IO;

namespace ExchangeCertManager.Services;

/// <summary>
/// "Install": copies this EXE to C:\Program Files\Andy Kemp Consulting\Exchange Certificate Manager and adds a
/// Start menu shortcut for everyone. Nothing else is installed; the data stays in C:\ProgramData\ExchangeCertManager.
/// The renewal task runs the installed copy, so it doesn't depend on wherever the EXE was downloaded to.
/// </summary>
public static class InstallService
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Exchange Certificate Manager.lnk");

    public static bool IsInstalled => File.Exists(AppPaths.InstalledExe);
    public static bool IsRunningInstalledCopy => AppPaths.IsInstalledCopy(Environment.ProcessPath);

    /// <summary>Copies this EXE into Program Files (replacing an older copy) and adds the Start menu shortcut.</summary>
    public static string Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's own path.");
        if (AppPaths.IsInstalledCopy(exe)) return exe;
        Directory.CreateDirectory(AppPaths.InstallDirectory);
        File.Copy(exe, AppPaths.InstalledExe, overwrite: true);
        try { CreateShortcut(); }
        catch (Exception ex) { Log.Warn($"Couldn't add the Start menu shortcut: {ex.Message}"); }
        Log.Success($"Installed to {AppPaths.InstalledExe}, with a Start menu shortcut");
        return AppPaths.InstalledExe;
    }

    private static void CreateShortcut()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell isn't available.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = AppPaths.InstalledExe;
        link.WorkingDirectory = AppPaths.InstallDirectory;
        link.Description = "Exchange Certificate Manager (Andy Kemp Consulting)";
        link.IconLocation = AppPaths.InstalledExe + ",0";
        link.Save();
    }

    /// <summary>Starts the installed copy; the caller closes this one.</summary>
    public static void StartInstalledCopy() =>
        Process.Start(new ProcessStartInfo(AppPaths.InstalledExe) { UseShellExecute = true, WorkingDirectory = AppPaths.InstallDirectory });
}
