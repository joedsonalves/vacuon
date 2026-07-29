using System.Diagnostics;
using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Optimization;

/// <summary>What the machine's memory looks like at one instant.</summary>
public sealed record MemoryReading
{
    public required long TotalBytes { get; init; }
    public required long AvailableBytes { get; init; }

    /// <summary>Physical memory in use. Total minus available, as Windows reports them.</summary>
    public long InUseBytes => TotalBytes - AvailableBytes;

    /// <summary>Percentage Windows itself reports, not one we derived.</summary>
    public required int LoadPercent { get; init; }

    /// <summary>
    /// Working set of the <c>Memory Compression</c> process.
    /// <para>
    /// Pages Windows compressed and kept in RAM instead of writing them to disk. It looks
    /// like waste in a process list and is the opposite: it is the reason those pages did
    /// not become disk reads.
    /// </para>
    /// </summary>
    public long CompressedBytes { get; init; }
}

/// <summary>One process, and what it is really holding.</summary>
public sealed record ProcessMemory
{
    public required int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Physical memory mapped in. What Task Manager shows, and shared pages inflate it.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>
    /// Memory this process alone is responsible for.
    /// <para>
    /// The honest figure for "who is using the RAM": working set counts pages shared with
    /// other processes once per process, so summing it across a browser's fifty children
    /// gives a total larger than the machine has.
    /// </para>
    /// </summary>
    public required long PrivateBytes { get; init; }

    public string? Path { get; init; }

    /// <summary>Name of the startup entry that launches this, when one does.</summary>
    public string? StartupEntryName { get; init; }

    public bool IsFromStartup => StartupEntryName is not null;
}

public sealed record MemoryReport
{
    public required MemoryReading Reading { get; init; }
    public required IReadOnlyList<ProcessMemory> TopProcesses { get; init; }

    /// <summary>How much of what is in use comes from programs Windows launches at sign-in.</summary>
    public long FromStartupBytes
    {
        get
        {
            long total = 0;
            foreach (ProcessMemory p in TopProcesses) if (p.IsFromStartup) total += p.PrivateBytes;
            return total;
        }
    }
}

/// <summary>
/// Measures memory. Changes nothing.
/// <para>
/// Cross-references the process list against the startup entries, because that is where the
/// only permanent answer lives: a program that is not launched at sign-in is not using memory
/// at sign-in, and no amount of emptying working sets achieves the same thing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MemoryScanner
{
    public MemoryReport Scan(int topCount = 20)
    {
        MemoryReading reading = ReadSystem();

        // Startup entries first, so each process can say whether something starts it.
        var startupPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (StartupEntry entry in new StartupScanner().Scan().Entries)
        {
            if (!entry.IsEnabled || entry.TargetPath is null) continue;
            startupPaths[entry.TargetPath] = entry.Name;
        }

        var processes = new List<ProcessMemory>();
        long compressed = 0;

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;

                if (name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
                {
                    compressed = process.WorkingSet64;
                    continue;
                }

                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is InvalidOperationException
                                             or System.ComponentModel.Win32Exception
                                             or NotSupportedException)
                {
                    // Protected or already gone. The row still counts, it just cannot be
                    // matched to a startup entry.
                }

                processes.Add(new ProcessMemory
                {
                    Id = process.Id,
                    Name = name,
                    WorkingSetBytes = process.WorkingSet64,
                    PrivateBytes = process.PrivateMemorySize64,
                    Path = path,
                    StartupEntryName = path is not null && startupPaths.TryGetValue(path, out string? entry)
                        ? entry
                        : null,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        // Grouped by name and ranked by private bytes: fifty browser children are one answer
        // to "what is using the memory", not fifty.
        List<ProcessMemory> top = [.. processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessMemory
            {
                Id = g.Count() == 1 ? g.First().Id : 0,
                Name = g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()})",
                WorkingSetBytes = g.Sum(p => p.WorkingSetBytes),
                PrivateBytes = g.Sum(p => p.PrivateBytes),
                Path = g.First().Path,
                StartupEntryName = g.Select(p => p.StartupEntryName).FirstOrDefault(n => n is not null),
            })
            .OrderByDescending(p => p.PrivateBytes)
            .Take(topCount)];

        return new MemoryReport
        {
            Reading = reading with { CompressedBytes = compressed },
            TopProcesses = top,
        };
    }

    private static MemoryReading ReadSystem()
    {
        var status = new Kernel32.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>(),
        };

        if (!Kernel32.GlobalMemoryStatusEx(ref status))
            return new MemoryReading { TotalBytes = 0, AvailableBytes = 0, LoadPercent = 0 };

        return new MemoryReading
        {
            TotalBytes = (long)status.ullTotalPhys,
            AvailableBytes = (long)status.ullAvailPhys,
            LoadPercent = (int)status.dwMemoryLoad,
        };
    }
}

/// <summary>What emptying a working set actually did.</summary>
public sealed record TrimResult(
    int ProcessesTouched,
    long AvailableBeforeBytes,
    long AvailableAfterBytes)
{
    /// <summary>
    /// How much the "available" figure moved.
    /// <para>
    /// Deliberately not called "freed". Nothing was freed: the pages went to the standby list
    /// or the pagefile, and come back — from disk — the moment their process touches them
    /// again. The interface shows this number next to that sentence, never alone.
    /// </para>
    /// </summary>
    public long MovedBytes => AvailableAfterBytes - AvailableBeforeBytes;
}

/// <summary>
/// Empties process working sets.
/// <para>
/// This is the operation every "RAM cleaner" is built on, and it is included here with its
/// real description attached rather than left out: there is a narrow honest use — forcing a
/// process that ballooned to give its pages back before something heavy starts — and there is
/// the marketing use, which is a number going up on a screen while the machine gets slower.
/// Vacuon does the first and refuses to word it like the second.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkingSetTrimmer
{
    public TrimResult TrimAll()
    {
        long before = Available();
        int touched = 0;

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                // The current process is skipped: trimming ourselves mid-measurement would
                // put Vacuon's own pages into the number it is about to report.
                if (process.Id == Environment.ProcessId) continue;

                if (Kernel32.EmptyWorkingSet(process.Handle)) touched++;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                         or System.ComponentModel.Win32Exception
                                         or NotSupportedException)
            {
                // Protected process, or one that exited. Skipped, not counted.
            }
            finally
            {
                process.Dispose();
            }
        }

        return new TrimResult(touched, before, Available());
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
