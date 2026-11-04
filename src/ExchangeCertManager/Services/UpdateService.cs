using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeCertManager.Services;

/// <summary>What downloads.andykemp.com says the latest version is (exchange-cert-manager/latest/version.json,
/// written by the build).</summary>
public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";

    public string PublishedText =>
        DateTimeOffset.TryParse(Published, out var d) ? d.ToLocalTime().ToString("d MMMM yyyy") : "";
}

/// <summary>
/// Checks downloads.andykemp.com for a newer version and updates the EXE in place.
/// The app is a single self-contained EXE, so there's no separate updater: Windows lets a running EXE be renamed, so
/// the new one is downloaded next to it, checked against the published SHA-256, the running one is renamed to .old
/// and the new one takes its name. The app then restarts, and the .old file is deleted on the next start. The path
/// doesn't change, so the Let's Encrypt renewal task (which runs this EXE) keeps working.
/// </summary>
public static class UpdateService
{
    public const string DownloadsPage = "https://downloads.andykemp.com/#exchange-cert-manager";
    private const string Base = "https://downloads.andykemp.com/exchange-cert-manager";
    private const string LatestUrl = Base + "/latest/version.json";

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string CurrentText => Current.ToString(3);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // System proxy settings are used by default, which is what a server behind a proxy needs
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ExchangeCertManager/{CurrentText}");
        return c;
    }

    /// <returns>The newer version, or null if this is the latest.</returns>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var info = JsonSerializer.Deserialize<UpdateInfo>(await resp.Content.ReadAsStringAsync(ct))
                   ?? throw new InvalidDataException("The version information couldn't be read.");
        if (!System.Version.TryParse(info.Version, out var latest))
            throw new InvalidDataException($"The published version \"{info.Version}\" isn't a version number.");
        return Normalise(latest) > Normalise(Current) ? info : null;
    }

    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Path of the running EXE, or null when it can't be updated in place (e.g. run from `dotnet`).</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return p is not null && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    /// <summary>Downloads the new version, checks it, and swaps it in. Returns the path to start.</summary>
    public static async Task<string> DownloadAndInstallAsync(UpdateInfo info, IProgress<(string Text, double? Percent)>? progress = null, CancellationToken ct = default)
    {
        var exe = ExePath ?? throw new InvalidOperationException("This copy can't update itself. Download the new version from downloads.andykemp.com.");
        if (!IsSha256(info.Sha256))
            throw new InvalidDataException("The published version has no SHA-256 checksum, so it wasn't installed. Download it from downloads.andykemp.com instead.");
        var name = string.IsNullOrWhiteSpace(info.File) ? "ExchangeCertManager.exe" : Path.GetFileName(info.File);
        var url = $"{Base}/{Uri.EscapeDataString(info.Version)}/{Uri.EscapeDataString(name)}";

        var dir = Path.GetDirectoryName(exe)!;
        var incoming = exe + ".new";
        var old = exe + ".old";

        progress?.Report(($"Downloading version {info.Version}…", 0));
        Log.Info($"Update: downloading {url}");
        try
        {
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total is > 0)
                    {
                        var percent = (int)(done * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(($"Downloading version {info.Version}… {done / 1048576.0:0.0} of {total.Value / 1048576.0:0.0} MB", percent));
                        }
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"Can't write to {dir}. Copy the app to a folder you can write to, such as C:\\ExchangeCertManager, and run it from there.");
        }

        progress?.Report(("Checking the SHA-256…", null));
        string actual;
        await using (var f = File.OpenRead(incoming))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(incoming);
            throw new InvalidDataException($"The download didn't match the published SHA-256 (expected {info.Sha256}, got {actual}), so it wasn't installed.");
        }
        Log.Info($"Update: SHA-256 matches ({actual})");

        progress?.Report(("Installing…", null));
        TryDelete(old);
        File.Move(exe, old);               // a running EXE can be renamed
        try { File.Move(incoming, exe); }
        catch
        {
            File.Move(old, exe);           // put the original back
            throw;
        }
        Log.Success($"Updated to version {info.Version}. The previous version is kept as {Path.GetFileName(old)} until the next start.");
        return exe;
    }

    /// <summary>Starts the updated EXE. The caller then closes this instance.</summary>
    public static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Removes what an earlier update left behind. Called on start, in the background.</summary>
    public static async Task CleanUpAsync()
    {
        var exe = ExePath;
        if (exe is null) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".new" })
        {
            // The old process may still be closing, so give it a few tries
            for (var i = 0; i < 10 && File.Exists(leftover); i++)
            {
                if (TryDelete(leftover)) break;
                await Task.Delay(1000);
            }
        }
    }

    private static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Couldn't open {url}: {ex.Message}"); }
    }
}
