using System.Diagnostics;
using Vacuon.Native.Interop;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Naming the program that has a file open, which is the difference between "this file could
/// not be copied" and something somebody can act on.
/// </summary>
public class RestartManagerTests
{
    [Fact]
    public void AFileThisProcessHasOpen_NamesThisProcess()
    {
        string path = Path.Combine(Path.GetTempPath(), "vacuon-rm-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                IReadOnlyList<FileHolder> holders = RestartManager.WhoHolds(path);

                Assert.NotEmpty(holders);
                Assert.Contains(holders, h => h.ProcessId == Environment.ProcessId);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileNobodyHasOpen_NamesNobody()
    {
        string path = Path.Combine(Path.GetTempPath(), "vacuon-rm-free-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            Assert.Empty(RestartManager.WhoHolds(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APathThatIsNotThere_AnswersNothingRatherThanThrowing()
    {
        // This runs while a failure is already being explained. A diagnostic that throws
        // while explaining an error is worse than no diagnostic.
        Assert.Empty(RestartManager.WhoHolds(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid())));
        Assert.Empty(RestartManager.WhoHolds(string.Empty));
        Assert.Empty(RestartManager.WhoHolds("   "));
    }

    [Fact]
    public void TheHolderHasAName()
    {
        string path = Path.Combine(Path.GetTempPath(), "vacuon-rm-name-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            FileHolder mine = RestartManager.WhoHolds(path)
                .First(h => h.ProcessId == Environment.ProcessId);

            Assert.False(string.IsNullOrWhiteSpace(mine.Name));
            Assert.False(mine.IsService);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
