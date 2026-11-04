using System.Security;
using System.Security.Principal;
using ExchangeCertManager.Models;

namespace ExchangeCertManager.Services;

/// <summary>Issue-and-deploy used by both the Let's Encrypt page and the scheduled renewal.</summary>
public static class AcmeDeployer
{
    public static async Task<string> IssueAndDeployAsync(CertificateService certs, AcmeService acme, AcmeRenewal def,
        AcmeChallengeType challenge, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var request = new AcmeIssueRequest(def.Domains, def.Email, def.Staging, challenge, def.Webroots);
        var (pfx, password, notAfter) = await acme.IssueAsync(request, progress, ct);
        var secure = CertificateService.ToSecure(password);
        var friendly = $"Let's Encrypt {def.Domains[0]} {DateTime.Now:yyyy-MM-dd}";

        string? thumbprint = null;
        foreach (var server in def.Servers)
        {
            progress?.Report($"Importing on {server}");
            thumbprint = await certs.ImportPfxAsync(server, pfx, secure, friendly);
            progress?.Report($"Assigning {string.Join(", ", def.Services)} on {server}");
            await certs.EnableAsync(server, thumbprint, def.Services);
        }

        if (def.RemoveOldCertificate && def.LastThumbprint is { Length: > 0 } old &&
            !old.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            var (authCurrent, authNext, _) = await certs.GetAuthConfigAsync();
            if (!old.Equals(authCurrent, StringComparison.OrdinalIgnoreCase) && !old.Equals(authNext, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var server in def.Servers)
                {
                    try
                    {
                        progress?.Report($"Removing the previous certificate on {server}");
                        await certs.RemoveAsync(server, old);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Couldn't remove old certificate {old} on {server}: {ex.Message}");
                    }
                }
            }
        }

        def.LastThumbprint = thumbprint;
        def.NotAfter = notAfter;
        def.LastRun = DateTime.Now;
        def.LastResult = $"Issued {thumbprint}";
        return thumbprint!;
    }
}

/// <summary>Headless mode: ExchangeCertManager.exe --renew-acme [--force]. Run by the scheduled task.</summary>
public static class RenewalRunner
{
    public const string Argument = "--renew-acme";
    public const int RenewWithinDays = 30;

    public static async Task<int> RunAsync(bool force)
    {
        Log.Info($"Scheduled Let's Encrypt renewal started as {WindowsIdentity.GetCurrent().Name}");
        var renewals = AcmeService.LoadRenewals();
        if (renewals.Count == 0)
        {
            Log.Info("No renewals are configured.");
            return 0;
        }

        var failures = 0;
        foreach (var def in renewals)
        {
            var due = force || def.NotAfter is null || def.NotAfter.Value - DateTime.Now < TimeSpan.FromDays(RenewWithinDays);
            if (!due)
            {
                Log.Info($"{def.DomainsText}: not due (expires {def.NotAfter:d})");
                continue;
            }

            if (def.Challenge == AcmeChallengeType.Dns01Manual)
            {
                Log.Warn($"{def.DomainsText}: uses manual DNS, so it can't renew unattended. Renew it from the Let's Encrypt page.");
                def.LastRun = DateTime.Now;
                def.LastResult = "Due: manual DNS, renew from the app";
                continue;
            }

            try
            {
                using var session = await ExchangeSession.ConnectAsync(def.ConnectServer, credential: null);
                var certs = new CertificateService(session);
                var progress = new Progress<string>(m => Log.Info(m));
                await AcmeDeployer.IssueAndDeployAsync(certs, new AcmeService(), def, def.Challenge, progress);
                Log.Success($"{def.DomainsText}: renewed");
            }
            catch (Exception ex)
            {
                failures++;
                def.LastRun = DateTime.Now;
                def.LastResult = $"Failed: {ex.Message}";
                Log.Error($"{def.DomainsText}: renewal failed: {ex.Message}");
            }
        }

        AcmeService.SaveRenewals(renewals);
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Registers the daily renewal task through the Task Scheduler COM API, so the account password
/// never appears on a command line.
/// </summary>
public static class RenewalTask
{
    public const string TaskName = "Exchange Certificate Manager - Let's Encrypt renewal";

    public static void Register(string userName, SecureString password)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's own path.");
        var start = DateTime.Today.AddDays(1).AddHours(3).AddMinutes(17).ToString("s");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Andy Kemp Consulting Ltd</Author>
                <Description>Renews Let's Encrypt certificates for Exchange when they're within {RenewalRunner.RenewWithinDays} days of expiry. Logs in {Log.LogDirectory}.</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                  <RandomDelay>PT45M</RandomDelay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userName)}</UserId>
                  <LogonType>Password</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{RenewalRunner.Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic scheduler = Activator.CreateInstance(type)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        const int TASK_CREATE_OR_UPDATE = 6;
        const int TASK_LOGON_PASSWORD = 1;
        var plain = new System.Net.NetworkCredential(string.Empty, password).Password;
        folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, userName, plain, TASK_LOGON_PASSWORD, null);
        Log.Success($"Scheduled task '{TaskName}' registered to run daily as {userName}");
    }

    public static bool Exists()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return false;
            dynamic scheduler = Activator.CreateInstance(type)!;
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            folder.GetTask(TaskName);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
