using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Vacuon.Core.Actions;

public enum RestorePointOutcome
{
    /// <summary>A new point exists that did not exist before. Verified, not assumed.</summary>
    Created,

    /// <summary>
    /// The call reported success and no new point appeared. Windows does this when one was
    /// made recently: the frequency limit is silent, and returns zero.
    /// </summary>
    NothingHappened,

    /// <summary>System Protection is off, so there is nothing to create a point on.</summary>
    Unavailable,

    /// <summary>Needs an elevated process, and this one is not.</summary>
    NeedsAdministrator,

    Failed,
}

/// <summary>What asking for a restore point actually produced.</summary>
public readonly record struct RestorePointResult(
    RestorePointOutcome Outcome,
    int SequenceBefore,
    int SequenceAfter,
    TimeSpan Took,
    string? Message = null)
{
    public bool Succeeded => Outcome == RestorePointOutcome.Created;
}

/// <summary>
/// A system restore point before a batch that could go wrong (PRD F7.8).
/// <para>
/// ⚠️ <b>Whether one was created is checked, never assumed.</b> Windows keeps a frequency
/// limit — by default one point per 24 hours — and when it declines for that reason
/// <c>CreateRestorePoint</c> <b>returns success and creates nothing</b>. An app that trusted
/// the return value would tell somebody they had a restore point on exactly the day they
/// most needed one. So the highest sequence number is read before and after, and only a
/// number that moved counts as a point.
/// </para>
/// <para>
/// ⚠️ It also does nothing at all when System Protection is turned off, which it is by
/// default on every drive but the system one, and on some machines on that one too. That is
/// reported as unavailable rather than as a failure: nothing went wrong, there was simply
/// nothing there to write to.
/// </para>
/// <para>
/// The work runs through the PowerShell already on the machine rather than through a WMI
/// dependency, the same way the shadow-copy figures are read.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class RestorePointService
{
    /// <summary>Creating one can take the better part of a minute on a busy machine.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The highest restore point sequence number, -1 when the list cannot be read.
    /// </summary>
    public static int LatestSequence()
    {
        string? output = Run(
            "$p = Get-CimInstance -ClassName SystemRestore -Namespace root/default -ErrorAction SilentlyContinue; " +
            "if ($null -eq $p) { 'none' } else { ($p | Measure-Object -Property SequenceNumber -Maximum).Maximum }",
            TimeSpan.FromSeconds(45));

        return ParseSequence(output);
    }

    /// <summary>Reads a sequence number, or -1 for anything that is not one.</summary>
    public static int ParseSequence(string? output)
    {
        string trimmed = (output ?? string.Empty).Trim();

        if (trimmed.Length == 0) return -1;

        // "none" means the list came back empty, which is a real answer: no points yet.
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : -1;
    }

    /// <summary>
    /// Asks Windows for a restore point and then checks whether it made one.
    /// </summary>
    public static RestorePointResult Create(string description)
    {
        var clock = Stopwatch.StartNew();

        int before = LatestSequence();
        if (before < 0)
        {
            clock.Stop();
            return new RestorePointResult(RestorePointOutcome.Unavailable, -1, -1, clock.Elapsed);
        }

        string safe = (description ?? "Vacuon").Replace("'", "''");

        // APPLICATION_INSTALL (0) with BEGIN_SYSTEM_CHANGE (100): the type Windows itself
        // uses before something that modifies the machine.
        string? output = Run(
            "$r = Invoke-CimMethod -Namespace root/default -ClassName SystemRestore " +
            $"-MethodName CreateRestorePoint -Arguments @{{ Description = '{safe}'; " +
            "RestorePointType = [uint32]0; EventType = [uint32]100 } -ErrorAction SilentlyContinue; " +
            "if ($null -eq $r) { 'failed' } else { $r.ReturnValue }",
            Timeout);

        int after = LatestSequence();
        clock.Stop();

        if (output is null || output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return new RestorePointResult(RestorePointOutcome.Failed, before, after, clock.Elapsed, output);

        // ⚠️ The return value is not the answer. The frequency limit makes this return zero
        // and create nothing, so the sequence number is what decides.
        if (after > before) return new RestorePointResult(RestorePointOutcome.Created, before, after, clock.Elapsed);

        return new RestorePointResult(RestorePointOutcome.NothingHappened, before, after, clock.Elapsed, output);
    }

    private static string? Run(string script, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                        or FileNotFoundException)
        {
            return null;
        }
    }
}
