using System.Security.Cryptography;
using Vacuon.Core.Actions;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Replacing a redundant copy with a second name for the one that stays (PRD F4.4).
/// <para>
/// Everything here runs against real files: the whole point of a hard link is what the file
/// system does with it, and a mock would be testing the mock.
/// </para>
/// </summary>
public class HardLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vacuon-link-tests-" + Guid.NewGuid().ToString("N"));

    public HardLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Pattern(int size, byte seed)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(seed + (i % 251));
        return data;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public void TheOldPathStillOpensAndStillReadsTheSameBytes()
    {
        // This is the whole promise: not a shortcut something has to know how to follow, but
        // another name on the same bytes. A program opening the old path cannot tell.
        byte[] content = Pattern(300_000, 9);
        string keeper = Write("fica.bin", content);
        string copy = Write("some.bin", content);

        string before = Hash(copy);

        LinkResult result = HardLinkService.Replace(keeper, copy);

        Assert.Equal(LinkOutcome.Linked, result.Outcome);
        Assert.True(File.Exists(copy));
        Assert.Equal(before, Hash(copy));
        Assert.Equal(content.Length, new FileInfo(copy).Length);
        Assert.Equal(content.Length, result.BytesFreed);
    }

    [Fact]
    public void TheTwoNamesBecomeOneFile()
    {
        // The change worth warning somebody about, kept in a test so it stays true on
        // purpose rather than by accident.
        byte[] content = Pattern(50_000, 3);
        string keeper = Write("a.bin", content);
        string copy = Write("b.bin", content);

        Assert.Equal(LinkOutcome.Linked, HardLinkService.Replace(keeper, copy).Outcome);

        using (var writer = new FileStream(keeper, FileMode.Open, FileAccess.Write))
        {
            writer.WriteByte(0xFF);
        }

        Assert.Equal(Hash(keeper), Hash(copy));

        // And removing one name leaves the other alone.
        File.Delete(copy);
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public void ContentThatNoLongerMatchesIsRefused_AndNothingIsTouched()
    {
        // The search hashed these minutes ago. This step does not move a file aside, it
        // replaces its contents with somebody else's, and there is no undo for that.
        string keeper = Write("keeper.bin", Pattern(40_000, 1));
        string copy = Write("copy.bin", Pattern(40_000, 1));

        File.WriteAllBytes(copy, Pattern(40_000, 2));   // changed since the plan
        string before = Hash(copy);

        LinkResult result = HardLinkService.Replace(keeper, copy);

        Assert.Equal(LinkOutcome.ContentChanged, result.Outcome);
        Assert.Equal(before, Hash(copy));
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public void DifferentLengthsAreRefusedWithoutReadingTheWholeThing()
    {
        string keeper = Write("k.bin", Pattern(40_000, 5));
        string copy = Write("c.bin", Pattern(41_000, 5));

        Assert.Equal(LinkOutcome.ContentChanged, HardLinkService.Replace(keeper, copy).Outcome);
        Assert.Equal(41_000, new FileInfo(copy).Length);
    }

    [Fact]
    public void APathUnderWindowsIsRefusedOnEitherSide()
    {
        // The protected list has no override, and adding a name to a protected file is
        // still touching it — so both ends are checked, not just the one being removed.
        string mine = Write("mine.bin", Pattern(1000, 1));

        Assert.Equal(LinkOutcome.Blocked,
                     HardLinkService.Replace(mine, @"C:\Windows\Installer\qualquer.msp").Outcome);

        Assert.Equal(LinkOutcome.Blocked,
                     HardLinkService.Replace(@"C:\Windows\Installer\qualquer.msp", mine).Outcome);

        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void DoingItTwiceFreesNothingTheSecondTime()
    {
        // Already one file under two names. Reporting the bytes again would be reporting
        // space that came back once as if it had come back twice.
        byte[] content = Pattern(20_000, 4);
        string keeper = Write("um.bin", content);
        string copy = Write("dois.bin", content);

        Assert.Equal(20_000, HardLinkService.Replace(keeper, copy).BytesFreed);

        LinkResult again = HardLinkService.Replace(keeper, copy);
        Assert.Equal(LinkOutcome.AlreadyLinked, again.Outcome);
        Assert.Equal(0, again.BytesFreed);
    }

    [Fact]
    public void AFileThatIsNotThereIsNotAFailure_ItIsAMissingFile()
    {
        string keeper = Write("existe.bin", Pattern(1000, 1));

        Assert.Equal(LinkOutcome.NotFound,
                     HardLinkService.Replace(keeper, Path.Combine(_root, "nunca-existiu.bin")).Outcome);
    }

    [Fact]
    public void NoScratchFileIsLeftBehind()
    {
        // The copy is renamed aside before the link is made, so a failure can put it back.
        // A success has to take that name away again, or the space never comes back.
        byte[] content = Pattern(30_000, 6);
        string keeper = Write("keep.bin", content);
        string copy = Write("drop.bin", content);

        Assert.Equal(LinkOutcome.Linked, HardLinkService.Replace(keeper, copy).Outcome);

        Assert.Empty(Directory.GetFiles(_root, "*.vacuon-link-*"));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }
}
