using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Optimization;

/// <summary>Where a startup item is declared.</summary>
public enum StartupSource
{
    RunUser,
    RunMachine,
    Run32Machine,
    StartupFolderUser,
    StartupFolderMachine,
}

/// <summary>One program Windows launches at sign-in.</summary>
public sealed record StartupEntry
{
    public required string Name { get; init; }

    /// <summary>Command line as declared, or the shortcut path for a Startup folder item.</summary>
    public required string Command { get; init; }

    public required StartupSource Source { get; init; }

    /// <summary>
    /// False when Windows has been told not to launch it.
    /// <para>
    /// Read from <c>StartupApproved</c> — the same switch Task Manager and Settings use, so
    /// what Vacuon shows is what those show.
    /// </para>
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>Executable the command points at, when one could be made out.</summary>
    public string? TargetPath { get; init; }
    public bool TargetExists { get; init; }

    /// <summary>Working set of this program's processes right now. Measured, never projected.</summary>
    public long MeasuredBytes { get; init; }
    public int RunningProcesses { get; init; }

    /// <summary>Machine-wide entries need Administrator to switch.</summary>
    public bool NeedsElevation =>
        Source is StartupSource.RunMachine or StartupSource.Run32Machine or StartupSource.StartupFolderMachine;

    public string SourceLabel => Source switch
    {
        StartupSource.RunUser => @"HKCU\...\Run",
        StartupSource.RunMachine => @"HKLM\...\Run",
        StartupSource.Run32Machine => @"HKLM\...\Run (32-bit)",
        StartupSource.StartupFolderUser => "Startup folder",
        _ => "Startup folder (all users)",
    };
}

public sealed record StartupReport
{
    public required IReadOnlyList<StartupEntry> Entries { get; init; }
    public required bool WasElevated { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public int EnabledCount
    {
        get
        {
            int n = 0;
            foreach (StartupEntry e in Entries) if (e.IsEnabled) n++;
            return n;
        }
    }

    /// <summary>Measured working set of the enabled ones that are running right now.</summary>
    public long MeasuredBytes
    {
        get
        {
            long total = 0;
            foreach (StartupEntry e in Entries) total += e.MeasuredBytes;
            return total;
        }
    }
}

/// <summary>
/// Working set per executable, keyed by full path, gathered once and shared.
/// <para>
/// Keyed by path rather than by process name, and that distinction was not academic: the
/// first version matched on name and credited 13 GiB of Opera — 54 processes the user had
/// opened by hand — to a startup entry that was switched off and had launched nothing.
/// </para>
/// <para>
/// Processes whose path cannot be read are skipped rather than guessed at. The number comes
/// out low rather than wrong, which is the right direction for a figure shown next to a
/// button that turns things off.
/// </para>
/// </summary>
internal static class ProcessFootprint
{
    public static Dictionary<string, (int Count, long Bytes)> Measure()
    {
        var map = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (InvalidOperationException) { return map; }

        foreach (Process process in all)
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;

                map.TryGetValue(path, out (int Count, long Bytes) current);
                map[path] = (current.Count + 1, current.Bytes + process.WorkingSet64);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                         or NotSupportedException
                                         or System.ComponentModel.Win32Exception)
            {
                // Ended mid-enumeration, or a protected process we may not open. Skipped.
            }
            finally
            {
                process.Dispose();
            }
        }

        return map;
    }
}

/// <summary>
/// Lists what Windows launches at sign-in, and what each one is holding in memory.
/// Reads only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupScanner
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Run32Key = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public StartupReport Scan()
    {
        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, (int Count, long Bytes)> processes = ProcessFootprint.Measure();

        var entries = new List<StartupEntry>();

        ReadRunKey(entries, RegistryHive.CurrentUser, RunKey, StartupSource.RunUser, "Run", processes);
        ReadRunKey(entries, RegistryHive.LocalMachine, RunKey, StartupSource.RunMachine, "Run", processes);
        ReadRunKey(entries, RegistryHive.LocalMachine, Run32Key, StartupSource.Run32Machine, "Run32", processes);

        ReadStartupFolder(entries, Environment.SpecialFolder.Startup,
                          StartupSource.StartupFolderUser, RegistryHive.CurrentUser, processes);
        ReadStartupFolder(entries, Environment.SpecialFolder.CommonStartup,
                          StartupSource.StartupFolderMachine, RegistryHive.LocalMachine, processes);

        stopwatch.Stop();

        return new StartupReport
        {
            Entries = entries,
            WasElevated = Elevation.IsElevated(),
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static void ReadRunKey(
        List<StartupEntry> into, RegistryHive hive, string subKey, StartupSource source,
        string approvedLeaf, Dictionary<string, (int Count, long Bytes)> processes)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(subKey);
            if (key is null) return;

            Dictionary<string, bool> approved = ReadApproved(root, approvedLeaf);

            foreach (string name in key.GetValueNames())
            {
                string command = key.GetValue(name)?.ToString() ?? string.Empty;
                if (command.Length == 0) continue;

                into.Add(Build(name, command, source, approved, processes, TargetOf(command)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ReadStartupFolder(
        List<StartupEntry> into, Environment.SpecialFolder folder, StartupSource source,
        RegistryHive hive, Dictionary<string, (int Count, long Bytes)> processes)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length == 0 || !Directory.Exists(path)) return;

            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            Dictionary<string, bool> approved = ReadApproved(root, "StartupFolder");

            foreach (string file in Directory.EnumerateFiles(path))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                into.Add(Build(name, file, source, approved, processes, FolderItemTarget(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static StartupEntry Build(
        string name, string command, StartupSource source,
        Dictionary<string, bool> approved, Dictionary<string, (int Count, long Bytes)> processes,
        string? target)
    {
        bool exists = target is not null && File.Exists(target);

        // Absent from StartupApproved means nobody ever switched it off.
        bool isEnabled = !approved.TryGetValue(name, out bool disabled) || !disabled;

        (int count, long bytes) = (0, 0L);

        // Only an enabled entry can be credited with anything running. A switched-off entry
        // started nothing, so whatever is running under that path was launched by the user
        // and is not this list's to claim.
        if (isEnabled && target is not null &&
            processes.TryGetValue(target, out (int Count, long Bytes) hit))
        {
            (count, bytes) = hit;
        }

        return new StartupEntry
        {
            Name = name,
            Command = command,
            Source = source,
            IsEnabled = isEnabled,
            TargetPath = target,
            TargetExists = exists,
            RunningProcesses = count,
            MeasuredBytes = bytes,
        };
    }

    /// <summary>
    /// Reads the on/off state Task Manager writes.
    /// <para>
    /// Twelve bytes: the first carries the state, the rest a timestamp of when it was switched
    /// off. Values seen in the wild include 0x02 and 0x06 for enabled and 0x03 for disabled,
    /// so the reliable test is the low bit — odd means off — rather than an equality check
    /// against 2 that would call <c>SecurityHealth</c> disabled.
    /// </para>
    /// </summary>
    internal static Dictionary<string, bool> ReadApproved(RegistryKey root, string leaf)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey? key = root.OpenSubKey($@"{ApprovedRoot}\{leaf}");
            if (key is null) return map;

            foreach (string name in key.GetValueNames())
                if (key.GetValue(name) is byte[] { Length: > 0 } data)
                    map[name] = IsDisabled(data);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return map;
    }

    internal static bool IsDisabled(byte[] data) => data.Length > 0 && (data[0] & 1) != 0;

    /// <summary>
    /// What a Startup folder item launches: the file a shortcut points at, or the item itself
    /// when it is not a shortcut.
    /// <para>
    /// ⚠️ It used to be the item's own path pushed through the command-line parser, which cut
    /// it at the first space — and every Startup folder has one, in "Start Menu". So every
    /// shortcut there was checked as <c>…\Windows\Start</c>, which does not exist, and was
    /// shown as pointing at a file that does not exist: all three on my machine, all three
    /// pointing at programs that are installed.
    /// </para>
    /// </summary>
    internal static string? FolderItemTarget(string item) =>
        string.Equals(Path.GetExtension(item), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLink.TargetOf(item)
            : item;

    /// <summary>
    /// The file a command line launches, found the way Windows finds it — or null when that
    /// cannot be told, which is never reported as missing.
    /// <list type="bullet">
    ///   <item>Environment variables expanded: a <c>REG_SZ</c> value keeps its <c>%…%</c>.</item>
    ///   <item>An unquoted path with spaces walked the way <c>CreateProcess</c> walks it,
    ///   space by space, until a file answers.</item>
    ///   <item>A bare name looked for where Windows looks: System32, Windows, the PATH.
    ///   <c>rundll32.exe …</c> was being checked against the current folder.</item>
    /// </list>
    /// "Missing" is only ever said of a path the app can name exactly.
    /// </summary>
    internal static string? TargetOf(string command, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;

        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? Locate(text[1..close], exists) : null;
        }

        // Shortest first, the order CreateProcess tries them in.
        for (int at = 0; at <= text.Length; at++)
        {
            if (at < text.Length && text[at] != ' ') continue;

            string candidate = text[..at];
            if (candidate.Length == 0) continue;

            if (Found(candidate, exists) is string found) return found;
        }

        // Nothing on the disk answers to any part of it. A path that can be named exactly is
        // a missing file; anything vaguer is not this app's to call missing.
        string? named = ExtractExecutable(text);
        if (named is null || !Path.IsPathRooted(named)) return null;

        return named.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !text.Contains(' ')
            ? named
            : null;
    }

    /// <summary>A quoted path: itself when rooted, wherever Windows would find it when not.</summary>
    private static string? Locate(string path, Func<string, bool> exists) =>
        Path.IsPathRooted(path) ? path : Found(path, exists);

    /// <summary>The file a candidate names, trying ".exe" after it the way Windows does.</summary>
    private static string? Found(string candidate, Func<string, bool> exists)
    {
        string[] names = Path.HasExtension(candidate) ? [candidate] : [candidate, candidate + ".exe"];

        foreach (string name in names)
        {
            if (Path.IsPathRooted(name))
            {
                if (exists(name)) return name;
                continue;
            }

            foreach (string folder in SearchFolders())
            {
                string full = Path.Combine(folder, name);
                if (exists(full)) return full;
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchFolders()
    {
        yield return Environment.SystemDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Path.IsPathRooted(folder)) yield return folder;
    }

    /// <summary>
    /// Pulls the executable out of a command line.
    /// <para>
    /// Quoted paths first, because an unquoted path with spaces cannot be split reliably —
    /// and guessing wrong would attribute somebody else's memory to this entry.
    /// </para>
    /// </summary>
    internal static string? ExtractExecutable(string command)
    {
        string text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        // Unquoted: take up to the first space that ends something looking like a path.
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return text[..(exe + 4)];

        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}

/// <summary>
/// Switches a startup program on or off.
/// <para>
/// Writes <c>StartupApproved</c> — exactly what Task Manager does — instead of deleting the
/// <c>Run</c> value. Deleting would work once and never come back: the command, its arguments
/// and its name would be gone, and "undo" would have nothing to restore. This way the entry
/// stays where the program put it and Windows is simply told to skip it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupSwitch
{
    public SwitchOutcome SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.NeedsElevation && !Elevation.IsElevated()) return SwitchOutcome.NeedsElevation;

        RegistryHive hive = entry.NeedsElevation ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

        string leaf = entry.Source switch
        {
            StartupSource.Run32Machine => "Run32",
            StartupSource.StartupFolderUser or StartupSource.StartupFolderMachine => "StartupFolder",
            _ => "Run",
        };

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey key = root.CreateSubKey($@"{StartupScanner.ApprovedRoot}\{leaf}", writable: true);

            key.SetValue(entry.Name, Payload(enabled), RegistryValueKind.Binary);

            // Read it back: "wrote it" and "it is there" are different claims.
            if (key.GetValue(entry.Name) is not byte[] check || StartupScanner.IsDisabled(check) == enabled)
                return SwitchOutcome.NotConfirmed;

            return SwitchOutcome.Applied;
        }
        catch (UnauthorizedAccessException)
        {
            return SwitchOutcome.NeedsElevation;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return SwitchOutcome.Failed;
        }
    }

    /// <summary>
    /// The twelve bytes Windows expects: state, then when it was switched off.
    /// </summary>
    internal static byte[] Payload(bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;

        // Enabled entries carry a zero timestamp, which is what Task Manager writes too.
        if (!enabled) BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());

        return data;
    }
}

internal static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
