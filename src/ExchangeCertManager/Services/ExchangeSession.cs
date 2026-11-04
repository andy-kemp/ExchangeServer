using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using Microsoft.Win32;

namespace ExchangeCertManager.Services;

public sealed class ExchangeCommandException : Exception
{
    public ExchangeCommandException(string command, string message) : base($"{command}: {message}") { }
}

/// <summary>
/// A remote PowerShell session to an Exchange server's /PowerShell endpoint.
///
/// The Exchange endpoint runs in no-language mode, so scripts and script blocks are rejected.
/// Every call here is sent as a single command with named parameters, which the endpoint accepts.
/// This is the reason the old WinForms script's Invoke-Command calls failed.
/// </summary>
public sealed class ExchangeSession : IDisposable
{
    private const string ExchangeShellUri = "http://schemas.microsoft.com/powershell/Microsoft.Exchange";

    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Pass as a parameter value to send an explicit $null (for example to clear a URL).</summary>
    public static readonly object Null = new();

    public string ConnectedServer { get; }
    public string ConnectedAs { get; }

    /// <summary>The credential used to connect, or null for the signed-in account. Reused for server-level tasks.</summary>
    public PSCredential? Credential { get; }

    private ExchangeSession(Runspace runspace, string server, string connectedAs, PSCredential? credential)
    {
        _runspace = runspace;
        ConnectedServer = server;
        ConnectedAs = connectedAs;
        Credential = credential;
    }

    /// <param name="credential">Null to use the signed-in Windows account (Kerberos).</param>
    public static async Task<ExchangeSession> ConnectAsync(string serverFqdn, PSCredential? credential, CancellationToken ct = default)
    {
        serverFqdn = serverFqdn.Trim();
        var uri = new Uri($"http://{serverFqdn}/PowerShell/");
        var info = new WSManConnectionInfo(uri, ExchangeShellUri, credential)
        {
            AuthenticationMechanism = AuthenticationMechanism.Kerberos,
            OpenTimeout = 60_000,
            OperationTimeout = 10 * 60_000,
            IdleTimeout = 60 * 60_000,
        };

        var runspace = RunspaceFactory.CreateRunspace(info);
        await Task.Run(runspace.Open, ct);

        var who = credential?.UserName ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        Log.Success($"Connected to {serverFqdn} as {who}");
        return new ExchangeSession(runspace, serverFqdn, who, credential);
    }

    /// <summary>
    /// Runs one Exchange cmdlet. A parameter with a null value is sent as a switch (e.g. -Force).
    /// Throws if the cmdlet writes any error.
    /// </summary>
    /// <param name="select">If set, the results are piped through Select-Object -Property, so they come back as plain
    /// property bags. Needed for Get-ExchangeCertificate: its objects are X509Certificate2s, which PowerShell 7 can
    /// turn back into empty certificates on this side, losing the thumbprint, names and dates.</param>
    public async Task<IReadOnlyList<PSObject>> InvokeAsync(string command, IDictionary<string, object?>? parameters = null,
        bool quiet = false, CancellationToken ct = default, IReadOnlyList<string>? select = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                ps.AddCommand(command);
                if (parameters is not null)
                {
                    foreach (var (name, value) in parameters)
                    {
                        if (value is null) ps.AddParameter(name);
                        else if (ReferenceEquals(value, Null)) ps.AddParameter(name, null);
                        else ps.AddParameter(name, value);
                    }
                }

                if (select is { Count: > 0 }) ps.AddCommand("Select-Object").AddParameter("Property", select.ToArray());

                if (!quiet) Log.Info($"> {Describe(command, parameters)}");

                Collection<PSObject> results;
                try
                {
                    results = ps.Invoke();
                }
                catch (RuntimeException ex)
                {
                    throw new ExchangeCommandException(command, ex.Message);
                }

                foreach (var w in ps.Streams.Warning) Log.Warn($"{command}: {w.Message}");

                if (ps.Streams.Error.Count > 0)
                {
                    var message = string.Join(" | ", ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).Distinct());
                    throw new ExchangeCommandException(command, message);
                }

                return (IReadOnlyList<PSObject>)results.Where(r => r is not null).ToList();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The command as it would be typed in the Exchange Management Shell (secrets masked).</summary>
    public static string Describe(string command, IDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return command;
        var parts = parameters.Select(p => p.Value switch
        {
            null => $"-{p.Key}",
            var v when ReferenceEquals(v, Null) => $"-{p.Key} $null",
            bool b => $"-{p.Key}:${b.ToString().ToLowerInvariant()}",
            DateTime d => $"-{p.Key} '{d:yyyy-MM-dd HH:mm}'",
            SecureString => $"-{p.Key} ********",
            byte[] b => $"-{p.Key} <{b.Length:N0} bytes>",
            string s => $"-{p.Key} '{s}'",
            IEnumerable e => e.Cast<object>().Any() ? $"-{p.Key} {string.Join(",", e.Cast<object>())}" : $"-{p.Key} @()",
            _ => $"-{p.Key} {p.Value}",
        });
        return $"{command} {string.Join(" ", parts)}";
    }

    public void Dispose()
    {
        try { _runspace.Close(); } catch { }
        _runspace.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// If this machine is an Exchange server, returns its FQDN so the connect screen can pre-fill it.
    /// </summary>
    public static string? DetectLocalExchangeServer()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\ExchangeServer\v15\Setup");
            if (key?.GetValue("MsiInstallPath") is null) return null;
            return Dns.GetHostEntry(string.Empty).HostName;
        }
        catch
        {
            return null;
        }
    }
}
