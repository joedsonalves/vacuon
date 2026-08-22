using System.Runtime.Versioning;
using Microsoft.Win32;
using Vacuon.Core.Index;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Analyzers;

/// <summary>A folder left behind by something that is no longer installed.</summary>
public sealed record Residue(
    string Folder,
    string Name,
    long Bytes,
    int FileCount,
    DateTime LastWriteUtc)
{
    public TimeSpan Age => DateTime.UtcNow - LastWriteUtc;
}

public sealed record ResidueReport(
    IReadOnlyList<Residue> Residues,
    int InstalledProgramsRead,
    int FoldersExamined)
{
    public long Bytes
    {
        get
        {
            long total = 0;
            foreach (Residue residue in Residues) total += residue.Bytes;
            return total;
        }
    }
}

/// <summary>
/// Folders under the per-user application-data roots that no installed program claims.
/// <para>
/// Uninstallers routinely leave settings, caches and logs behind — that is often deliberate,
/// so a reinstall finds your configuration. The cost is that a machine accumulates folders for
/// programs removed years ago, and nothing ever tells you.
/// </para>
/// <para>
/// <b>The evidence is a name, and a name is not an identity.</b> Everything here is a guess
/// dressed in the least confident language that is still useful: it compares folder names
/// against the display names and install locations of what the registry says is installed. A
/// program whose folder is called something else entirely will be reported as residue and
/// will be wrong. So nothing is deleted and nothing is pre-ticked.
/// </para>
/// <para>
/// There is deliberately <b>no confidence grade</b> on a row. One was drafted and taken out:
/// the code has only one kind of evidence to offer, so a "likely" beside some rows and a
/// "possible" beside others would be a distinction the app cannot actually make — a certainty
/// invented to make a guess look better sorted.
/// </para>
/// <para>
/// <b>Where it will not look.</b> Only the three per-user roots, never Program Files, never
/// anything under Windows, and never anything <see cref="ProtectedPaths"/> refuses. A wrong
/// guess about a folder in AppData costs settings; the same wrong guess about a folder in
/// Program Files costs an installed program.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UninstallResidue
{
    /// <summary>
    /// Folders smaller than this are not reported. A leftover holding two kilobytes of
    /// settings is not what anyone opened this screen for, and listing it buries what is.
    /// </summary>
    public const long MinimumBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Nothing touched more recently than this is reported. A program uninstalled today may
    /// be reinstalled tomorrow, and a folder written to last week probably has an owner.
    /// </summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(90);

    private static readonly string[] Keys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>
    /// Names that belong to the platform rather than to any one program, and would otherwise
    /// be reported because nothing in the uninstall list claims them.
    /// </summary>
    private static readonly string[] NeverResidue =
    [
        "Microsoft", "Windows", "Packages", "Temp", "Programs", "Google", "Mozilla",
        "NVIDIA", "NVIDIA Corporation", "Intel", "AMD", "CrashDumps", "ConnectedDevicesPlatform",
        "D3DSCache", "IconCache", "PlaceholderTileLogoFolder", "Comms", "History",
        "INetCache", "INetCookies", "WebCache", "VirtualStore", "ElevatedDiagnostics",
    ];

    /// <summary>
    /// Reads the index for folders under the user roots that nothing installed claims.
    /// <para>
    /// <b>From the index, not by walking the disk.</b> The first version enumerated AppData
    /// recursively and took minutes — on a machine whose whole point is that it already holds
    /// an MFT index with every file's parent, size and last write in it. Walking the file
    /// system to learn what the index already knew was using the wrong tool in the one
    /// application that has the right one.
    /// </para>
    /// </summary>
    public static ResidueReport Find(VolumeIndex index, long minimumBytes = MinimumBytes,
                                     TimeSpan? minimumAge = null)
    {
        TimeSpan age = minimumAge ?? MinimumAge;
        DateTime cutoff = DateTime.UtcNow - age;

        HashSet<string> installed = InstalledNames(out int programsRead);

        // Which index entries are the roots themselves, and which top-level folder under one
        // of them each file belongs to. Walking up from each file is cheap because the index
        // stores the parent as an array position.
        var rootIndices = new HashSet<int>();

        foreach (string root in Roots())
        {
            int found = FindFolder(index, root);
            if (found >= 0) rootIndices.Add(found);
        }

        if (rootIndices.Count == 0)
            return new ResidueReport([], programsRead, 0);

        // Candidate folder index to what accumulated under it.
        var totals = new Dictionary<int, (long Bytes, int Files, long LastWrite)>();
        var candidateOf = new Dictionary<int, int>();   // any folder -> the candidate above it
        var examined = new HashSet<int>();

        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;

            int candidate = CandidateAbove(index, (int)entry.ParentIndex, rootIndices, candidateOf);
            if (candidate < 0) continue;

            examined.Add(candidate);

            totals.TryGetValue(candidate, out (long Bytes, int Files, long LastWrite) current);

            long written = entry.LastWriteUtc;

            totals[candidate] = (current.Bytes + entry.LogicalSize, current.Files + 1,
                                 written > current.LastWrite ? written : current.LastWrite);
        }

        var residues = new List<Residue>();

        foreach ((int folder, (long bytes, int files, long lastWrite)) in totals)
        {
            if (bytes < minimumBytes) continue;

            string name = index.GetName(folder).ToString();

            if (IsNeverResidue(name)) continue;
            if (Claimed(name, installed)) continue;

            DateTime written = lastWrite <= 0 ? DateTime.UtcNow : DateTime.FromFileTimeUtc(lastWrite);
            if (written > cutoff) continue;

            string path = index.GetFullPath(folder);
            if (path.Length == 0) continue;

            // The one list nothing bypasses, checked against the actual path.
            if (ProtectedPaths.Check(path).IsProtected) continue;

            residues.Add(new Residue(path, name, bytes, files, written));
        }

        residues.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

        return new ResidueReport(residues, programsRead, examined.Count);
    }

    /// <summary>
    /// The top-level folder under one of the roots that contains this one, or -1.
    /// <para>
    /// Memoised as it climbs, so a root with fifty thousand files under it is walked once
    /// rather than once per file.
    /// </para>
    /// </summary>
    private static int CandidateAbove(VolumeIndex index, int folder, HashSet<int> roots,
                                      Dictionary<int, int> memo)
    {
        if (memo.TryGetValue(folder, out int known)) return known;

        var climbed = new List<int>();
        int current = folder;
        int answer = -1;

        // Bounded: a corrupt index could otherwise present a cycle of parents.
        for (int depth = 0; depth < 256; depth++)
        {
            if (memo.TryGetValue(current, out int cached)) { answer = cached; break; }

            climbed.Add(current);

            int parent = (int)index.Entries[current].ParentIndex;

            if (roots.Contains(current)) { answer = -2; break; }   // the root itself, not under one
            if (roots.Contains(parent)) { answer = current; break; }
            if (parent == current) break;                          // reached the volume root

            current = parent;
        }

        if (answer == -2) answer = -1;

        foreach (int step in climbed) memo[step] = answer;

        return answer;
    }

    /// <summary>
    /// The index position of a folder by path, or -1.
    /// <para>
    /// The leaf name is compared first and the full path only for the few entries whose name
    /// matches. Building a path for every directory on a real volume means millions of string
    /// builds to find three folders.
    /// </para>
    /// <para>
    /// Both sides are trimmed of a trailing separator before comparing:
    /// <c>GetFullPath</c> ends a directory with one and <c>GetFolderPath</c> does not, and
    /// that single character was enough to find no roots at all and report an empty list
    /// forever.
    /// </para>
    /// </summary>
    private static int FindFolder(VolumeIndex index, string path)
    {
        string wanted = path.TrimEnd(Path.DirectorySeparatorChar);
        string leaf = Path.GetFileName(wanted);

        if (leaf.Length == 0) return -1;

        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            if (!entries[i].IsInUse || !entries[i].IsDirectory) continue;
            if (!index.GetName(i).Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;

            string full = index.GetFullPath(i).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(full, wanted, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>The three per-user roots, and deliberately nothing else.</summary>
    public static IEnumerable<string> Roots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    public static bool IsNeverResidue(string name)
    {
        foreach (string known in NeverResidue)
            if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Whether anything installed plausibly owns a folder of this name.
    /// <para>
    /// Deliberately generous. Every match here is a folder <b>not</b> reported, and the cost
    /// of missing one is that somebody keeps a folder they might have removed. The cost of a
    /// false report is somebody deleting the settings of a program they still use.
    /// </para>
    /// </summary>
    public static bool Claimed(string folderName, IReadOnlySet<string> installed)
    {
        if (installed.Contains(folderName)) return true;

        foreach (string program in installed)
        {
            if (program.Length < 4 || folderName.Length < 4) continue;

            if (program.Contains(folderName, StringComparison.OrdinalIgnoreCase)) return true;
            if (folderName.Contains(program, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Display names and install-location leaf names of everything registered.</summary>
    private static HashSet<string> InstalledNames(out int count)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        count = 0;

        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string path in Keys)
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(path);
                    if (key is null) continue;

                    foreach (string child in key.GetSubKeyNames())
                    {
                        using RegistryKey? entry = key.OpenSubKey(child);
                        if (entry is null) continue;

                        count++;

                        if (entry.GetValue("DisplayName") is string display && display.Length > 0)
                        {
                            names.Add(display);

                            // "Vacuon 0.4.0" should also claim a folder called "Vacuon".
                            foreach (string word in display.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                if (word.Length >= 4) names.Add(word);
                        }

                        if (entry.GetValue("Publisher") is string publisher && publisher.Length > 0)
                            names.Add(publisher);

                        if (entry.GetValue("InstallLocation") is string location && location.Length > 0)
                        {
                            try { names.Add(Path.GetFileName(location.TrimEnd('\\'))); }
                            catch (ArgumentException) { }
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        names.Remove(string.Empty);
        return names;
    }

}
