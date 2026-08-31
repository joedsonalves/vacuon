using Vacuon.Core.Analyzers;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// The space that belongs to Windows rather than to a file somebody chose to keep (F1.9).
/// </summary>
public class SystemSpaceTests
{
    [Fact]
    public void ThreeNumbersAreThreeNumbers()
    {
        // Measured on a real machine: 5,939,675,136 used of 6,319,783,936 allocated, ceiling
        // 10,218,373,120. The same figures vssadmin prints as "5,53 GB (1%)".
        SystemSpaceItem item = SystemSpace.Parse("C:\\", "5939675136 6319783936 10218373120");

        Assert.Equal(SystemSpaceKind.ShadowCopies, item.Kind);
        Assert.Equal(5_939_675_136, item.Bytes);
        Assert.Equal("6319783936/10218373120", item.Detail);
        Assert.True(item.IsKnown);
    }

    [Fact]
    public void NoShadowStorageIsZero_NotUnknown()
    {
        // A volume with no shadow storage really does hold nothing, and saying so is an
        // answer. It is not the same as not having been able to ask.
        SystemSpaceItem item = SystemSpace.Parse("D:\\", "none");

        Assert.Equal(0, item.Bytes);
        Assert.True(item.IsKnown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Access denied.")]
    [InlineData("5939675136")]
    [InlineData("5939675136 6319783936")]
    [InlineData("5,53 GB 5,89 GB 9,52 GB")]
    public void AnythingThatIsNotThreeIntegersIsNotAnAnswer(string output)
    {
        // ⚠️ The last one is what parsing vssadmin's own printout would hand over: English
        // labels, a comma for a decimal point, and a unit. Reading "5" out of "5,53 GB"
        // would report five bytes of shadow storage. Unknown says unknown.
        SystemSpaceItem item = SystemSpace.Parse("C:\\", output);

        Assert.False(item.IsKnown);
        Assert.Equal(-1, item.Bytes);
    }

    [Fact]
    public void TheAnswerFromThisMachineIsReadable()
    {
        // Runs the real query. Its numbers depend on the machine, so what is asserted is the
        // shape: either a figure that makes sense, or an honest "could not ask".
        SystemSpaceItem item = SystemSpace.ShadowCopies("C:\\");

        Assert.Equal(SystemSpaceKind.ShadowCopies, item.Kind);
        Assert.True(item.Bytes == -1 || item.Bytes >= 0);
    }
}
