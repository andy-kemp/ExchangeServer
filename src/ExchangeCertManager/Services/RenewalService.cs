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

/// <summary>What the registered task looks like now.</summary>
public sealed record RenewalTaskInfo(string UserId, bool IsGmsa, DateTime? LastRun, int LastResult, DateTime? NextRun, string State, string ExePath);

/// <summary>
/// Registers the daily renewal task through the Task Scheduler COM API, so the account password
/// never appears on a command line. The task can run as a normal service account (with its password) or as a
/// group Managed Service Account (gMSA, name ending in $), where Windows fetches the password from AD itself.
/// Registering again replaces the task, which is how an existing task moves to a gMSA.
/// </summary>
public static class RenewalTask
{
    public const string TaskName = "Exchange Certificate Manager - Let's Encrypt renewal";

    /// <param name="password">The account's password, or null for a gMSA.</param>
    /// <param name="exe">The EXE the task runs (the installed copy, normally).</param>
    public static void Register(string userName, SecureString? password, string exe)
    {
        var gmsa = password is null;
        if (gmsa && !userName.EndsWith('$')) userName += "$";
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

        dynamic folder = Folder();
        const int TASK_CREATE_OR_UPDATE = 6;
        const int TASK_LOGON_PASSWORD = 1;
        // For a gMSA the password is left empty: Task Scheduler gets it from AD when the task runs
        // (the same as Register-ScheduledTask with -LogonType Password and no password).
        var plain = password is null ? null : new System.Net.NetworkCredential(string.Empty, password).Password;
        folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, userName, plain, TASK_LOGON_PASSWORD, null);
        Log.Success($"Scheduled task '{TaskName}' registered to run daily as {userName}{(gmsa ? " (gMSA)" : "")}");
    }

    public static bool Exists() => Info() is not null;

    /// <summary>The registered task's account and last result, or null if there isn't one.</summary>
    public static RenewalTaskInfo? Info()
    {
        try
        {
            dynamic task = Folder().GetTask(TaskName);
            string user = task.Definition.Principal.UserId ?? "";
            DateTime last = task.LastRunTime;
            DateTime next = task.NextRunTime;
            int result = task.LastTaskResult;
            int state = task.State;
            string exe = "";
            try { exe = task.Definition.Actions.Item(1).Path ?? ""; } catch { }
            static DateTime? Real(DateTime d) => d.Year < 2000 ? null : d;
            var stateText = state switch { 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown" };
            return new RenewalTaskInfo(user, user.EndsWith('$'), Real(last), result, Real(next), stateText, exe);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a gMSA in AD. Its sAMAccountName is at most 15 characters plus $, so a longer name given to
    /// New-ADServiceAccount is cut short (gMSA_ExchangeCert becomes gMSA_ExchangeCe$); the short form is tried too.
    /// </summary>
    /// <returns>The DOMAIN\name$ Windows knows it by.</returns>
    public static string ResolveGmsa(string name)
    {
        name = name.Trim();
        var domain = name.Contains('\\') ? name[..name.IndexOf('\\')] : Environment.UserDomainName;
        var account = (name.Contains('\\') ? name[(name.IndexOf('\\') + 1)..] : name).TrimEnd('$');
        var candidates = new List<string> { account };
        if (account.Length > 15) candidates.Add(account[..15]);
        foreach (var c in candidates)
        {
            try
            {
                var sid = new NTAccount(domain, c + "$").Translate(typeof(SecurityIdentifier));
                return ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            }
            catch (IdentityNotMappedException) { }
        }
        throw new InvalidOperationException(
            $"AD has no account {domain}\\{account}$." +
            (account.Length > 15 ? $" gMSA names are cut to 15 characters ({account[..15]}$), and that wasn't found either." : "") +
            " Use Create gMSA to set one up, or check the name with Get-ADServiceAccount (its SamAccountName).");
    }

    /// <summary>Starts the task now (as its own account), to prove it works.</summary>
    public static void RunNow()
    {
        dynamic task = Folder().GetTask(TaskName);
        task.Run(null);
        Log.Info($"Started '{TaskName}'. Its log lines go to {Log.LogDirectory}.");
    }

    /// <summary>Plain-English meaning of the common Task Scheduler results.</summary>
    public static string ResultText(int code) => unchecked((uint)code) switch
    {
        0 => "succeeded",
        1 => "finished with errors (see the log)",
        0x41300 => "ready",
        0x41301 => "running",
        0x41303 => "hasn't run yet",
        0x8007052E => "couldn't log on: for a gMSA, check this computer is allowed to retrieve its password and it has 'Log on as a batch job'",
        0x80070569 => "the account isn't allowed to log on as a batch job here",
        0x800710E0 => "was refused by the operator or administrator",
        _ => $"returned 0x{code:X8}",
    };

    private static dynamic Folder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic scheduler = Activator.CreateInstance(type)!;
        scheduler.Connect();
        return scheduler.GetFolder("\\");
    }
}
