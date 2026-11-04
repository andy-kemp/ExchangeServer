using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExchangeCertManager.Services;

public sealed record GmsaSetupOptions(
    string Name,
    /// <summary>True: every Exchange server may use it ("Exchange Servers" group). False: only this server.</summary>
    bool AllExchangeServers,
    /// <summary>Lab only: create the KDS root key back-dated 10 hours so it can be used straight away.</summary>
    bool BackdateKdsRootKey,
    bool CreateRoleGroup);

public sealed record GmsaSetupResult(bool Ready, string Account, string Message);

/// <summary>
/// Creates (or reuses) the group Managed Service Account the renewal task runs as, and gives it what it needs:
/// the KDS root key if the forest has none, the account itself, this server allowed to use it, installed and tested
/// here, local Administrators, and a least-privilege Exchange role group ("Certificate renewal": Exchange Server
/// Certificates and View-Only Configuration). AD work runs in Windows PowerShell with the ActiveDirectory module
/// (installed if missing); the role group is created over the Exchange session. Needs rights to create accounts in
/// AD (Domain Admins, or delegated) and Organization Management.
/// </summary>
public static class GmsaSetupService
{
    public const string RoleGroupName = "Certificate renewal";
    private static readonly Regex ValidName = new("^[A-Za-z0-9_-]{1,15}$");

    public static string? ValidateName(string name) =>
        ValidName.IsMatch(name) ? null
            : "Use up to 15 letters, digits, - or _ (AD limits a gMSA's account name to 15 characters), without the domain or $.";

    public static async Task<GmsaSetupResult> RunAsync(ExchangeSession exchange, GmsaSetupOptions o, IProgress<string>? progress = null)
    {
        if (ValidateName(o.Name) is { } bad) throw new ArgumentException(bad);

        // 1. The Exchange role group, over the Exchange session (least privilege for renewal)
        if (o.CreateRoleGroup)
        {
            progress?.Report($"Checking the Exchange role group '{RoleGroupName}'");
            var existing = await exchange.InvokeAsync("Get-RoleGroup", new Dictionary<string, object?> { ["Filter"] = $"Name -eq '{RoleGroupName}'" }, quiet: true);
            if (existing.Count == 0)
            {
                progress?.Report($"Creating the Exchange role group '{RoleGroupName}'");
                await exchange.InvokeAsync("New-RoleGroup", new Dictionary<string, object?>
                {
                    ["Name"] = RoleGroupName,
                    ["Roles"] = new[] { "Exchange Server Certificates", "View-Only Configuration" },
                    ["Description"] = "Let's Encrypt renewal task (Exchange Certificate Manager): manage Exchange certificates, read configuration.",
                });
            }
        }

        // 2. Everything in AD and on this server, in Windows PowerShell
        var script = BuildScript(o);
        var (exit, output) = await RunWindowsPowerShellAsync(script, progress);
        var result = output.LastOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (output.Any(l => l == "WAITKDS"))
            return new GmsaSetupResult(false, "",
                "The forest had no KDS root key, so one has been created. Domain controllers can't hand out gMSA passwords until it's 10 hours old (Microsoft's safeguard for replication). Run Create gMSA again after that, or, in a lab, tick 'back-date the key' to use it now.");
        if (exit != 0 || result is null)
        {
            var error = output.LastOrDefault(l => l.StartsWith("ERROR ", StringComparison.Ordinal))?[6..] ?? $"Windows PowerShell exited with {exit}.";
            throw new InvalidOperationException(error);
        }

        // RESULT <DOMAIN\sam$> <True|False>
        var parts = result.Split(' ');
        var account = parts[1];
        var ok = parts.Length > 2 && parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
        return new GmsaSetupResult(ok, account, ok
            ? $"{account} is ready: this server can use it, it's a local administrator here{(o.CreateRoleGroup ? $" and a member of the '{RoleGroupName}' role group" : "")}."
            : $"{account} was created, but Test-ADServiceAccount says this server can't use it yet. If this server was only just allowed (through the Exchange Servers group), restart it or run 'klist -li 0x3e7 purge', then try again.");
    }

    private static string BuildScript(GmsaSetupOptions o)
    {
        var roleGroupBlock = o.CreateRoleGroup
            ? $"'STEP Adding it to the {RoleGroupName} role group'\n" +
              $"$rg = Get-ADGroup -Filter \"Name -eq '{RoleGroupName}'\"\n" +
              "if ($rg) { Add-ADGroupMember -Identity $rg -Members $gmsa } else { 'STEP The role group isn''t in AD yet; add the gMSA to it later' }"
            : "";
        return $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            $name = '{{o.Name}}'
            if (-not (Get-Module -ListAvailable ActiveDirectory)) {
                'STEP Installing the Active Directory PowerShell module (RSAT-AD-PowerShell)'
                Install-WindowsFeature RSAT-AD-PowerShell | Out-Null
            }
            Import-Module ActiveDirectory -Verbose:$false
            $domain = Get-ADDomain

            if (-not (Get-KdsRootKey)) {
                if (${{(o.BackdateKdsRootKey ? "true" : "false")}}) {
                    'STEP Creating the KDS root key, back-dated so it can be used now (lab only)'
                    Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10)) | Out-Null
                } else {
                    'STEP Creating the KDS root key'
                    Add-KdsRootKey -EffectiveImmediately | Out-Null
                    'WAITKDS'
                    return
                }
            }

            $allowed = {{(o.AllExchangeServers ? "Get-ADGroup -Identity 'Exchange Servers'" : "Get-ADComputer -Identity $env:COMPUTERNAME")}}
            $gmsa = Get-ADServiceAccount -Filter "Name -eq '$name'" -Properties PrincipalsAllowedToRetrieveManagedPassword
            if ($gmsa) {
                "STEP $name already exists; making sure this server may use it"
                $current = @($gmsa.PrincipalsAllowedToRetrieveManagedPassword)
                if ($current -notcontains $allowed.DistinguishedName) {
                    Set-ADServiceAccount -Identity $gmsa -PrincipalsAllowedToRetrieveManagedPassword ($current + $allowed.DistinguishedName)
                }
            } else {
                "STEP Creating the gMSA $name"
                New-ADServiceAccount -Name $name -DNSHostName "$name.$($domain.DNSRoot)" `
                    -PrincipalsAllowedToRetrieveManagedPassword $allowed `
                    -Description 'Let''s Encrypt renewal task for Exchange Certificate Manager' -Enabled $true
                $gmsa = Get-ADServiceAccount -Identity $name
            }
            $sam = $gmsa.SamAccountName
            $account = "$($domain.NetBIOSName)\$sam"

            'STEP Installing it on this server'
            try { Install-ADServiceAccount -Identity $sam } catch { "STEP Install-ADServiceAccount: $($_.Exception.Message)" }

            'STEP Adding it to the local Administrators group'
            try { Add-LocalGroupMember -Group Administrators -Member $account } catch { if ($_.Exception.Message -notmatch 'already a member') { throw } }

            {{roleGroupBlock}}

            $ok = Test-ADServiceAccount -Identity $sam
            "RESULT $account $ok"
        } catch {
            "ERROR $($_.Exception.Message)"
            exit 1
        }
        """;
    }

    private static async Task<(int Exit, List<string> Output)> RunWindowsPowerShellAsync(string script, IProgress<string>? progress)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var lines = new List<string>();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start Windows PowerShell.");
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lines) lines.Add(e.Data);
            if (e.Data.StartsWith("STEP ", StringComparison.Ordinal))
            {
                progress?.Report(e.Data[5..]);
                Log.Info(e.Data[5..]);
            }
            else if (e.Data.StartsWith("ERROR ", StringComparison.Ordinal)) Log.Error($"gMSA setup: {e.Data[6..]}");
        };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn($"gMSA setup: {e.Data}"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        lock (lines) return (p.ExitCode, lines.ToList());
    }
}
