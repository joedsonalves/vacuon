using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Optimization;

public enum TerminateOutcome
{
    /// <summary>Gone, and confirmed gone.</summary>
    Closed,
    /// <summary>Refused by <see cref="ProtectedProcesses"/>. Never attempted.</summary>
    Protected,
    /// <summary>Already gone by the time we got there.</summary>
    NotFound,
    /// <summary>No permission — usually a process running at a higher integrity level.</summary>
    AccessDenied,
    /// <summary>The kill was issued and the process is still there.</summary>
    StillRunning,
    Failed,
}

public sealed record TerminateResult
{
    public required string Name { get; init; }
    public required TerminateOutcome Outcome { get; init; }

    /// <summary>How many of the group actually went.</summary>
    public int ClosedCount { get; init; }
    public int AttemptedCount { get; init; }

    /// <summary>Private memory those processes were holding, measured just before the kill.</summary>
    public long HeldBytes { get; init; }

    public long AvailableBeforeBytes { get; init; }
    public long AvailableAfterBytes { get; init; }

    /// <summary>
    /// How much the machine's available memory actually rose.
    /// <para>
    /// Reported alongside <see cref="HeldBytes"/> rather than instead of it, because they are
    /// different facts and rarely identical: Windows reclaims the pages, but other processes
    /// and the cache take some of it back within the same second. Showing only the flattering
    /// one of the two would be the sort of arithmetic this app exists not to do.
    /// </para>
    /// </summary>
    public long AvailableRoseBytes => AvailableAfterBytes - AvailableBeforeBytes;

    public bool Succeeded => Outcome == TerminateOutcome.Closed;
    public string? Message { get; init; }
}

/// <summary>
/// Processes Vacuon will not kill, ever.
/// <para>
/// Same rule as <c>Safety/ProtectedPaths</c>: no override, no "advanced mode", no checkbox.
/// Several of these take the machine down instantly and without a chance to save anything —
/// terminating <c>csrss</c>, <c>wininit</c> or <c>lsass</c> is an immediate stop error, not an
/// error message. A memory panel is not a reason to hand somebody that.
/// </para>
/// </summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Killing any of these bugchecks the machine or logs the user out with no warning.
        "System", "Idle", "Registry", "Secure System", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "LsaIso", "fontdrvhost", "dwm", "svchost",
        "Memory Compression", "MemCompression", "WerFault", "sihost", "ctfmon",

        // Vacuon itself: a button that closes the window it lives in is not a feature.
        "Vacuon", "vacuon",
    };

    public static bool IsProtected(string processName)
    {
        // The list groups by display name, which carries a count: "opera (54)".
        int paren = processName.IndexOf(" (", StringComparison.Ordinal);
        string bare = paren > 0 ? processName[..paren] : processName;

        return Names.Contains(bare.Trim());
    }
}

/// <summary>
/// Closes processes, and reports what closing them actually gave back.
/// <para>
/// Unlike emptying a working set, this really does free memory — the pages are gone, not
/// moved — so "freed" is an honest word here. It is also the destructive one of the two:
/// anything unsaved in those processes is lost, which is why the interface makes the caller
/// ask twice.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessTerminator
{
    /// <summary>How long to wait for a process to actually go before calling it still running.</summary>
    private const int ExitWaitMs = 4000;

    /// <summary>Closes every process sharing a name, and confirms they are gone.</summary>
    public TerminateResult CloseByName(string displayName)
    {
        if (ProtectedProcesses.IsProtected(displayName))
            return new TerminateResult { Name = displayName, Outcome = TerminateOutcome.Protected };

        int paren = displayName.IndexOf(" (", StringComparison.Ordinal);
        string name = (paren > 0 ? displayName[..paren] : displayName).Trim();

        long before = Available();

        Process[] targets;
        try { targets = Process.GetProcessesByName(name); }
        catch (InvalidOperationException ex)
        {
            return new TerminateResult
            {
                Name = displayName, Outcome = TerminateOutcome.Failed, Message = ex.Message,
            };
        }

        if (targets.Length == 0)
            return new TerminateResult { Name = displayName, Outcome = TerminateOutcome.NotFound };

        long held = 0;
        int closed = 0;
        bool denied = false;

        foreach (Process process in targets)
        {
            try
            {
                // Measured before the kill, because afterwards there is nothing left to ask.
                held += process.PrivateMemorySize64;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(ExitWaitMs);

                if (process.HasExited) closed++;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                denied = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // Exited between the enumeration and the kill. Nothing to do, and nothing
                // to claim credit for either.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Give Windows a moment to reclaim before measuring, or the figure reads low for a
        // reason that has nothing to do with the process.
        Thread.Sleep(250);

        TerminateOutcome outcome = closed > 0
            ? TerminateOutcome.Closed
            : denied ? TerminateOutcome.AccessDenied : TerminateOutcome.StillRunning;

        return new TerminateResult
        {
            Name = displayName,
            Outcome = outcome,
            ClosedCount = closed,
            AttemptedCount = targets.Length,
            HeldBytes = held,
            AvailableBeforeBytes = before,
            AvailableAfterBytes = Available(),
        };
    }

    private static long Available()
    {
        var status = new Kernel32.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>(),
        };

        return Kernel32.GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 0;
    }
}
