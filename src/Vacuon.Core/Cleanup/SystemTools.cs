using System.Diagnostics;
using System.Runtime.Versioning;

namespace Vacuon.Core.Cleanup;

/// <summary>What a Windows tool did when Vacuon ran it.</summary>
public sealed record ToolResult(
    string ToolId,
    bool Ran,
    int ExitCode,
    string Output,
    long FreedBytes,
    bool FreedBytesMeasured,
    string? Error = null)
{
    public bool Succeeded => Ran && ExitCode == 0;
}

/// <summary>
/// Runs the tools Windows ships for jobs Vacuon must not do by hand.
/// <para>
/// This exists because of a rule the app does not bend: <see cref="Safety.ProtectedPaths"/>
/// refuses everything under <c>%WINDIR%</c>, with no override. That refusal is right — the
/// component store is not a folder of junk, it is servicing state, and tools that "clean"
/// it by deleting files are how people end up unable to install the next update. So the
/// Windows folder is cleaned by Microsoft's own tools or not at all.
/// </para>
/// <para>
/// <b>Free space is measured, not taken from the tool's word.</b> The volume's free space is
/// read before and after, and the difference is what gets reported. DISM prints no total,
/// vssadmin prints one that counts differently, and the catalog's "typical gain" is a range
/// from someone else's machine. Only the before-and-after is about this disk.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemTools
{
    private readonly Func<string, string, TimeSpan, (int ExitCode, string Output)> _run;
    private readonly Func<string, long> _freeSpace;

    /// <param name="runner">Injected so tests can assert on the command line without running it.</param>
    /// <param name="freeSpace">Injected for the same reason.</param>
    public SystemTools(
        Func<string, string, TimeSpan, (int, string)>? runner = null,
        Func<string, long>? freeSpace = null)
    {
        _run = runner ?? RunProcess;
        _freeSpace = freeSpace ?? FreeSpaceOf;
    }

    /// <summary>Ids the catalog may reference.</summary>
    public static IReadOnlyList<string> KnownTools =>
    [
        "dism.componentCleanup",
        "powercfg.hibernateOff",
        "vssadmin.deleteOldShadows",
    ];

    public ToolResult Run(string toolId, string volumeRoot = "C:\\",
                          CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (string exe, string args, TimeSpan timeout) = toolId switch
        {
            "dism.componentCleanup" => (
                "dism.exe",
                "/Online /Cleanup-Image /StartComponentCleanup",
                // DISM on a machine that has not had this run in years takes a long while,
                // and killing it halfway is worse than waiting.
                TimeSpan.FromMinutes(60)),

            "powercfg.hibernateOff" => (
                "powercfg.exe", "/hibernate off", TimeSpan.FromMinutes(2)),

            "vssadmin.deleteOldShadows" => (
                "vssadmin.exe", $"delete shadows /for={volumeRoot.TrimEnd('\\')} /oldest /quiet",
                TimeSpan.FromMinutes(10)),

            _ => (string.Empty, string.Empty, TimeSpan.Zero),
        };

        if (exe.Length == 0)
            return new ToolResult(toolId, false, -1, string.Empty, 0, false, "unknown tool id");

        long before = _freeSpace(volumeRoot);

        try
        {
            (int exitCode, string output) = _run(exe, args, timeout);

            long after = _freeSpace(volumeRoot);
            long freed = after - before;

            // Both readings have to have worked for the difference to mean anything, and a
            // negative one means something else on the machine wrote while the tool ran —
            // in which case there is no honest figure to report.
            bool measured = before > 0 && after > 0 && freed >= 0;

            return new ToolResult(toolId, true, exitCode, output.Trim(),
                                  measured ? freed : 0, measured);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or TimeoutException)
        {
            return new ToolResult(toolId, false, -1, string.Empty, 0, false, ex.Message);
        }
    }

    private static (int, string) RunProcess(string exe, string args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"could not start {exe}");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{exe} did not finish within {timeout}");
        }

        return (process.ExitCode, output + error);
    }

    private static long FreeSpaceOf(string volumeRoot)
    {
        try { return new DriveInfo(volumeRoot).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
