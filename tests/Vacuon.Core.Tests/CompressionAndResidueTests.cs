using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M8 — compression candidates.
/// <para>
/// The estimate is the only forward-looking number in the report, and the tests here are
/// mostly about the categories that are <b>absent</b>: offering to compress a folder of video
/// spends CPU on every read and gives back nothing, and doing it with an honest 2% beside it
/// would still be doing it.
/// </para>
/// </summary>
public class CompressionCandidateTests
{
    [Theory]
    [InlineData(FileCategories.Log)]
    [InlineData(FileCategories.Code)]
    [InlineData(FileCategories.Document)]
    [InlineData(FileCategories.Database)]
    public void TypesThatCompressWellHaveARatio(string category)
    {
        Assert.NotNull(CompressionCandidates.RatioFor(category));
    }

    [Theory]
    [InlineData(FileCategories.Video)]
    [InlineData(FileCategories.Image)]
    [InlineData(FileCategories.Audio)]
    [InlineData(FileCategories.Archive)]
    [InlineData(FileCategories.Installer)]
    public void TypesThatAreAlreadyCompressedAreNotOffered(string category)
    {
        // Absent rather than present with a small ratio. A row on the screen is an offer.
        Assert.Null(CompressionCandidates.RatioFor(category));
    }

    [Fact]
    public void EveryRatioIsAFractionOfWhatIsThere()
    {
        // A ratio at or above 1 would promise back everything, or more than everything, which
        // is the family of arithmetic that once reported 758 GiB on a 476 GiB volume.
        foreach (string category in new[]
        {
            FileCategories.Log, FileCategories.Code, FileCategories.Document,
            FileCategories.Database, FileCategories.Build, FileCategories.Executable,
        })
        {
            double ratio = CompressionCandidates.RatioFor(category)!.Value;

            Assert.True(ratio > 0 && ratio < 1, $"{category} has a ratio of {ratio}");
        }
    }

    [Fact]
    public void TheEstimateIsBytesTimesTheRatio()
    {
        var candidate = new CompressionCandidate(@"C:\logs", 100, 1_000_000, FileCategories.Log, 0.75);

        Assert.Equal(750_000, candidate.EstimatedSaving);
    }

    [Fact]
    public void TheEstimateIsNamedAnEstimate()
    {
        // Not decoration. The number is bytes times a typical ratio, never a trial
        // compression of these files, and the property that carries it says so at every call
        // site. This app does not state figures it did not measure without labelling them.
        Assert.Contains("Estimated",
            nameof(CompressionCandidate.EstimatedSaving), StringComparison.Ordinal);
    }
}

/// <summary>
/// Milestone M8, F3.6 — folders left behind by programs that are gone.
/// <para>
/// Every row is a guess built on a name, so these tests are about the guard rails: what the
/// matcher refuses to report, and how generously it decides something is still owned.
/// </para>
/// </summary>
public class UninstallResidueTests
{
    private static IReadOnlySet<string> Installed(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AFolderMatchingAnInstalledProgramIsNotResidue()
    {
        Assert.True(UninstallResidue.Claimed("Vacuon", Installed("Vacuon")));
    }

    [Fact]
    public void MatchingIgnoresCase()
    {
        Assert.True(UninstallResidue.Claimed("vacuon", Installed("Vacuon")));
    }

    [Fact]
    public void AVersionedDisplayNameStillClaimsThePlainFolder()
    {
        // "Vacuon 0.4.0" in the uninstall list owns a folder called "Vacuon".
        Assert.True(UninstallResidue.Claimed("Vacuon", Installed("Vacuon 0.4.0")));
    }

    [Fact]
    public void APublisherFolderIsClaimedByItsProgram()
    {
        Assert.True(UninstallResidue.Claimed("JetBrains", Installed("JetBrains Rider 2026.1")));
    }

    [Fact]
    public void SomethingNothingClaimsIsReportable()
    {
        Assert.False(UninstallResidue.Claimed("SomeDeadApp", Installed("Vacuon", "Notepad++")));
    }

    [Fact]
    public void ShortNamesDoNotMatchOnSubstrings()
    {
        // Without a length floor, a three-letter folder matches half the uninstall list and
        // the feature silently reports nothing at all.
        Assert.False(UninstallResidue.Claimed("abc", Installed("Fabricator")));
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Packages")]
    [InlineData("Temp")]
    [InlineData("CrashDumps")]
    [InlineData("NVIDIA")]
    public void PlatformFoldersAreNeverResidue(string name)
    {
        // These belong to Windows or to drivers rather than to any one program, so nothing in
        // the uninstall list claims them and every one of them would be reported.
        Assert.True(UninstallResidue.IsNeverResidue(name));
    }

    [Fact]
    public void AnOrdinaryProgramFolderIsNotOnTheNeverList()
    {
        Assert.False(UninstallResidue.IsNeverResidue("SomeDeadApp"));
    }

    [Fact]
    public void ItLooksOnlyInsideTheUserProfileRoots()
    {
        // A wrong guess in AppData costs settings. The same wrong guess in Program Files
        // costs an installed program, so it never looks there.
        string[] roots = [.. UninstallResidue.Roots()];

        Assert.NotEmpty(roots);

        foreach (string root in roots)
        {
            Assert.DoesNotContain("Program Files", root, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\Windows", root, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheInstalledProgramListIsActuallyReadable()
    {
        // Read-only, against this machine's registry. If nothing comes back, every folder
        // looks unclaimed and the feature reports the whole of AppData as residue — the
        // failure that matters here is not an empty list but a list of everything.
        var index = SyntheticIndex.WithNothingUnderTheRoots();

        ResidueReport report = UninstallResidue.Find(index);

        Assert.True(report.InstalledProgramsRead > 0, "no installed programs were read");
    }

    [Fact]
    public void AnOldUnclaimedFolderIsReported()
    {
        VolumeIndex index = SyntheticIndex.Build(
            root: SyntheticIndex.LocalAppData,
            folders: [("SomeDeadApp", 40L * 1024 * 1024, DateTime.UtcNow.AddYears(-2))]);

        ResidueReport report = UninstallResidue.Find(index);

        Assert.Equal("SomeDeadApp", Assert.Single(report.Residues).Name);
    }

    [Fact]
    public void AFolderWrittenToRecentlyIsLeftAlone()
    {
        // Something used last week has an owner, whatever the uninstall list says.
        VolumeIndex index = SyntheticIndex.Build(
            root: SyntheticIndex.LocalAppData,
            folders: [("SomeDeadApp", 40L * 1024 * 1024, DateTime.UtcNow.AddDays(-3))]);

        Assert.Empty(UninstallResidue.Find(index).Residues);
    }

    [Fact]
    public void ASmallFolderIsNotWorthReporting()
    {
        VolumeIndex index = SyntheticIndex.Build(
            root: SyntheticIndex.LocalAppData,
            folders: [("SomeDeadApp", 200 * 1024, DateTime.UtcNow.AddYears(-2))]);

        Assert.Empty(UninstallResidue.Find(index).Residues);
    }

    [Fact]
    public void APlatformFolderIsNotReportedEvenWhenOldAndLarge()
    {
        VolumeIndex index = SyntheticIndex.Build(
            root: SyntheticIndex.LocalAppData,
            folders: [("Microsoft", 4L * 1024 * 1024 * 1024, DateTime.UtcNow.AddYears(-5))]);

        Assert.Empty(UninstallResidue.Find(index).Residues);
    }

    [Fact]
    public void TheReportedSizeIsWhatIsUnderTheFolder()
    {
        VolumeIndex index = SyntheticIndex.Build(
            root: SyntheticIndex.LocalAppData,
            folders: [("SomeDeadApp", 40L * 1024 * 1024, DateTime.UtcNow.AddYears(-2))]);

        Residue residue = Assert.Single(UninstallResidue.Find(index).Residues);

        Assert.Equal(40L * 1024 * 1024, residue.Bytes);
        Assert.True(residue.FileCount > 0);
    }
}

/// <summary>
/// A hand-built index, so the residue tests state a known answer rather than whatever this
/// particular machine happens to hold.
/// </summary>
internal static class SyntheticIndex
{
    public static string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static VolumeIndex WithNothingUnderTheRoots() => Build(LocalAppData, []);

    /// <summary>
    /// Builds root → folder → one file per named folder, with the whole size on that file.
    /// </summary>
    public static VolumeIndex Build(string root, (string Name, long Bytes, DateTime Written)[] folders)
    {
        // The root path is split into its components so GetFullPath rebuilds it exactly; the
        // finder locates the roots by comparing full paths.
        string[] parts = root.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var entries = new List<FileEntry>();
        var names = new NameBlob();

        // Entry 0 is the volume root and is its own parent, as the real index has it.
        entries.Add(Folder(names, parts[0], parent: 0, record: 0));

        int parentIndex = 0;

        for (int i = 1; i < parts.Length; i++)
        {
            entries.Add(Folder(names, parts[i], parentIndex, (uint)entries.Count));
            parentIndex = entries.Count - 1;
        }

        foreach ((string name, long bytes, DateTime written) in folders)
        {
            entries.Add(Folder(names, name, parentIndex, (uint)entries.Count));
            int folderIndex = entries.Count - 1;

            string fileName = "payload.bin";

            entries.Add(new FileEntry
            {
                RecordNumber = (uint)entries.Count,
                ParentIndex = (uint)folderIndex,
                NameOffset = names.Append(fileName),
                NameLength = (ushort)fileName.Length,
                Flags = EntryFlags.None,
                LogicalSize = bytes,
                AllocatedSize = bytes,
                LastWriteUtc = written.ToFileTimeUtc(),
                HardLinkCount = 1,
            });
        }

        var volume = new VolumeInfo('C', "Test", "NTFS", 1_000_000_000_000, 500_000_000_000, 4096, false);

        return new VolumeIndex([.. entries], names, volume, ScanStrategy.Win32Walk);
    }

    private static FileEntry Folder(NameBlob names, string name, int parent, uint record) => new()
    {
        RecordNumber = record,
        ParentIndex = (uint)parent,
        NameOffset = names.Append(name),
        NameLength = (ushort)name.Length,
        Flags = EntryFlags.Directory,
        HardLinkCount = 1,
    };
}
