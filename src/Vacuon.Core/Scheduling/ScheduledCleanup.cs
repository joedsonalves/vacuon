using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Core.Cleanup;

namespace Vacuon.Core.Scheduling;

/// <summary>How often a scheduled cleanup runs.</summary>
public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>One scheduled task Vacuon owns.</summary>
public sealed record ScheduledTask(
    string Name,
    string Schedule,
    string NextRun,
    string Status,
    string Command);

public sealed record ScheduleResult(bool Succeeded, string Output, string? Error = null);

/// <summary>
/// The result of asking Windows what Vacuon has scheduled.
/// <para>
/// Carries <see cref="Succeeded"/> so that a query that failed cannot be mistaken for a
/// machine with nothing scheduled on it. The two look identical in a bare list, and only
/// one of them is something the app actually knows.
/// </para>
/// </summary>
public sealed record ScheduleListing(
    bool Succeeded,
    IReadOnlyList<ScheduledTask> Tasks,
    string? Error = null);

/// <summary>
/// Creates and removes Windows scheduled tasks that run a cleanup profile.
/// <para>
/// Driven through <c>schtasks.exe</c> rather than the Task Scheduler COM API. The COM route
/// needs a large interop surface for something this small, and the command line is stable,
/// scriptable and — the part that matters here — <b>readable</b>: what Vacuon scheduled can
/// be inspected and deleted with a tool the user already has, without this app's help.
/// </para>
/// <para>
/// <b>A scheduled run can only quarantine.</b> Not the Recycle Bin, not permanent deletion.
/// Unattended removal is the one thing in this app nobody is present to stop, so the only
/// disposal it may use is the reversible one — <see cref="Build"/> hard-codes it rather than
/// taking it as a parameter, so no caller can widen it later by passing a different value.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledCleanup
{
    /// <summary>Every task Vacuon creates carries this prefix, so listing can find them.</summary>
    public const string TaskPrefix = "Vacuon";

    private readonly Func<string, (int ExitCode, string Output)> _run;

    /// <param name="runner">Injected so tests can assert on the command line without scheduling anything.</param>
    public ScheduledCleanup(Func<string, (int, string)>? runner = null) =>
        _run = runner ?? RunSchtasks;

    /// <summary>
    /// The command line a scheduled run executes.
    /// <para>
    /// The disposal is fixed at quarantine and the profile is limited to the two that
    /// exclude dangerous rules. A schedule that could run <c>--profile=custom --to=permanent</c>
    /// would be a way to arrange, in advance, for files to disappear with nobody watching.
    /// </para>
    /// </summary>
    public static string Build(string executablePath, CleanupProfile profile)
    {
        string name = profile == CleanupProfile.Deep ? "deep" : "quick";
        return $"\"{executablePath}\" clean --profile={name} --apply --to=quarantine";
    }

    public ScheduleResult Create(string executablePath, ScheduleFrequency frequency,
                                 TimeOnly at, CleanupProfile profile, string? taskName = null)
    {
        if (profile == CleanupProfile.Custom)
        {
            return new ScheduleResult(false, string.Empty,
                "a scheduled run may not use the custom profile: it can include dangerous rules");
        }

        string name = taskName ?? $"{TaskPrefix}\\{(profile == CleanupProfile.Deep ? "Deep" : "Quick")}Cleanup";
        string schedule = frequency switch
        {
            ScheduleFrequency.Weekly => "WEEKLY",
            ScheduleFrequency.Monthly => "MONTHLY",
            _ => "DAILY",
        };

        string command = Build(executablePath, profile);

        // /F overwrites a task of the same name; without it re-running this prompts, and a
        // prompt in a non-interactive context hangs forever.
        string arguments =
            $"/Create /F /TN \"{name}\" /TR \"{command.Replace("\"", "\\\"")}\" " +
            $"/SC {schedule} /ST {at:HH\\:mm}";

        (int exit, string output) = _run(arguments);

        return exit == 0
            ? new ScheduleResult(true, output.Trim())
            : new ScheduleResult(false, output.Trim(), $"schtasks exited {exit}");
    }

    /// <summary>
    /// Every task under Vacuon's own folder.
    /// <para>
    /// It asks for the whole list and filters by prefix rather than passing the folder to
    /// <c>/TN</c>, because schtasks does not accept a folder there — it exits 255. Reporting
    /// that as an empty list would have the app announce "nothing scheduled" about a machine
    /// it failed to read, which is the one thing this project treats as a bug rather than a
    /// rough edge. Hence <see cref="ScheduleListing.Succeeded"/>: absent and unknown are
    /// different answers, and the caller is told which one it got.
    /// </para>
    /// </summary>
    public ScheduleListing List()
    {
        (int exit, string output) = _run("/Query /FO CSV /V");

        var tasks = new List<ScheduledTask>();
        string prefix = "\\" + TaskPrefix + "\\";

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = SplitCsv(line);
            if (fields.Length < 9) continue;

            // The header repeats once per task folder, not just at the top.
            if (fields[1] == "TaskName") continue;

            if (!fields[1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            tasks.Add(new ScheduledTask(
                Name: fields[1].TrimStart('\\'),
                Schedule: fields.Length > 19 ? fields[19] : string.Empty,
                NextRun: fields[2],
                Status: fields[3],
                Command: fields[8]));
        }

        // A found task proves the query worked, whatever exit code came back: enumerating
        // every folder on a real machine trips over tasks it cannot read, and schtasks
        // reports that in the exit code even though the rest of the list arrived intact.
        bool succeeded = exit == 0 || tasks.Count > 0;

        return new ScheduleListing(succeeded, tasks,
            succeeded ? null : $"schtasks exited {exit}: {output.Trim()}");
    }

    public ScheduleResult Delete(string taskName)
    {
        (int exit, string output) = _run($"/Delete /F /TN \"{taskName}\"");

        return exit == 0
            ? new ScheduleResult(true, output.Trim())
            : new ScheduleResult(false, output.Trim(), $"schtasks exited {exit}");
    }

    /// <summary>Splits one CSV line, honouring quoted fields that contain commas.</summary>
    internal static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (char c in line.Trim('\r'))
        {
            if (c == '"') { quoted = !quoted; continue; }

            if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    private static (int, string) RunSchtasks(string arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException("could not start schtasks.exe");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit(60_000);

            return (process.ExitCode, output + error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
