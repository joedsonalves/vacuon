using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vacuon.Core.Optimization;

/// <summary>What the machine says about one component right now.</summary>
public sealed record AiComponentStatus
{
    public required AiComponent Component { get; init; }
    public required ComponentState State { get; init; }

    /// <summary>Raw value read from the registry, or null when nothing was set.</summary>
    public int? RawValue { get; init; }

    /// <summary>
    /// Bytes of working set held by this component's processes at the moment of the scan.
    /// <para>
    /// Measured, never estimated. Zero means nothing of this component is running — which is
    /// the honest thing to show, and not the same as "turning this off saves nothing".
    /// </para>
    /// </summary>
    public long MeasuredBytes { get; init; }

    public int RunningProcesses { get; init; }

    /// <summary>Versions found, for the entries Vacuon only reports.</summary>
    public IReadOnlyList<string> PackagesFound { get; init; } = [];

    public bool IsOn => State == ComponentState.On;
    public bool CanAct => Component.IsActionable && State is ComponentState.On or ComponentState.Off;
}

public sealed record AiScanReport
{
    public required IReadOnlyList<AiComponentStatus> Items { get; init; }
    public required bool WasElevated { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Components present and still switched on.</summary>
    public int OnCount
    {
        get
        {
            int n = 0;
            foreach (AiComponentStatus s in Items) if (s.IsOn) n++;
            return n;
        }
    }

    /// <summary>Total measured working set of everything running. Zero is a real answer.</summary>
    public long MeasuredBytes
    {
        get
        {
            long total = 0;
            foreach (AiComponentStatus s in Items) total += s.MeasuredBytes;
            return total;
        }
    }
}

/// <summary>
/// Reads the state of every catalogued component. Touches nothing.
/// <para>
/// Separate from <see cref="AiComponentSwitch"/> by design: the scan is safe to run at any
/// time and needs no elevation for the parts it can reach, while changing anything is a
/// deliberate, journalled act.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AiComponentScanner
{
    private const string PackageRepository =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public AiScanReport Scan()
    {
        var stopwatch = Stopwatch.StartNew();

        // One pass over the process list: asking per component would walk it a dozen times.
        Dictionary<string, (int Count, long Bytes)> processes = MeasureProcesses();
        List<string> packages = ReadPackageNames();

        var items = new List<AiComponentStatus>(AiComponentCatalog.All.Count);

        foreach (AiComponent component in AiComponentCatalog.All)
        {
            items.Add(component.Control == ControlKind.Package
                ? InspectPackage(component, packages, processes)
                : InspectRegistry(component, processes));
        }

        stopwatch.Stop();

        return new AiScanReport
        {
            Items = items,
            WasElevated = IsElevated(),
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static AiComponentStatus InspectRegistry(
        AiComponent component, Dictionary<string, (int Count, long Bytes)> processes)
    {
        int? raw = ReadValue(component);

        // No value at all means Windows is using its own default, which for every entry here
        // is "on". Saying Unknown would be needlessly vague; saying Off would be a lie.
        ComponentState state = raw is null
            ? ComponentState.On
            : raw == component.OffValue ? ComponentState.Off
            : raw == component.OnValue ? ComponentState.On
            : ComponentState.Unknown;

        (int count, long bytes) = Sum(component, processes);

        return new AiComponentStatus
        {
            Component = component,
            State = state,
            RawValue = raw,
            RunningProcesses = count,
            MeasuredBytes = bytes,
        };
    }

    private static AiComponentStatus InspectPackage(
        AiComponent component, List<string> packages,
        Dictionary<string, (int Count, long Bytes)> processes)
    {
        var found = new List<string>();

        foreach (string name in packages)
            if (name.StartsWith(component.PackagePrefix!, StringComparison.OrdinalIgnoreCase))
                found.Add(name);

        (int count, long bytes) = Sum(component, processes);

        return new AiComponentStatus
        {
            Component = component,
            State = found.Count > 0 ? ComponentState.On : ComponentState.Absent,
            PackagesFound = found,
            RunningProcesses = count,
            MeasuredBytes = bytes,
        };
    }

    private static (int Count, long Bytes) Sum(
        AiComponent component, Dictionary<string, (int Count, long Bytes)> processes)
    {
        int count = 0;
        long bytes = 0;

        foreach (string name in component.ProcessNames)
        {
            if (!processes.TryGetValue(name, out (int Count, long Bytes) hit)) continue;
            count += hit.Count;
            bytes += hit.Bytes;
        }

        return (count, bytes);
    }

    private static int? ReadValue(AiComponent component)
    {
        if (component.Hive is null || component.SubKey is null || component.ValueName is null)
            return null;

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(component.Hive.Value, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(component.SubKey);

            return key?.GetValue(component.ValueName) as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static List<string> ReadPackageNames()
    {
        var names = new List<string>();

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(PackageRepository);
            if (key is null) return names;

            names.AddRange(key.GetSubKeyNames());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return names;
    }

    /// <summary>
    /// Working set per process name, in one pass.
    /// <para>
    /// Working set rather than private bytes because it is what Task Manager shows, and a
    /// number the user can check against something they already trust.
    /// </para>
    /// </summary>
    private static Dictionary<string, (int Count, long Bytes)> MeasureProcesses()
    {
        var map = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (InvalidOperationException) { return map; }

        foreach (Process process in all)
        {
            try
            {
                string name = process.ProcessName;
                long bytes = process.WorkingSet64;

                map.TryGetValue(name, out (int Count, long Bytes) current);
                map[name] = (current.Count + 1, current.Bytes + bytes);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // The process ended between the enumeration and the read. Nothing to record.
            }
            finally
            {
                process.Dispose();
            }
        }

        return map;
    }

    private static bool IsElevated()
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
