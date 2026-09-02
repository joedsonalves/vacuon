using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Finding the next occurrence inside the file being edited — the answer both the preview
/// pane and the editor window ask for.
/// </summary>
public class FindInTextTests
{
    [Fact]
    public void TheFirstMatchIsFoundFromTheStart()
    {
        TextMatch match = FindInText.Next("port 9222 open", "9222", 0);

        Assert.True(match.Found);
        Assert.Equal(5, match.Index);
        Assert.Equal(4, match.Length);
        Assert.Equal(1, match.Total);
    }

    [Fact]
    public void SearchingResumesAfterTheCaret()
    {
        // 9222 twice; asking from just past the first one has to reach the second.
        TextMatch match = FindInText.Next("9222 and 9222", "9222", 1);

        Assert.Equal(9, match.Index);
        Assert.Equal(2, match.Total);
    }

    [Fact]
    public void PastTheLastMatchItComesBackAround()
    {
        // ⚠️ Not "no matches". Somebody on the last occurrence pressing Enter again is
        // asking to keep going, and answering "none" about a word that is plainly on the
        // screen is the app contradicting what the person can see.
        TextMatch match = FindInText.Next("9222 and 9222", "9222", 12);

        Assert.True(match.Found);
        Assert.Equal(0, match.Index);
    }

    [Fact]
    public void CaseDoesNotDecideWhetherSomethingExists()
    {
        Assert.True(FindInText.Next("HTTP header", "http", 0).Found);
    }

    [Fact]
    public void NothingIsFoundWhenNothingIsThere()
    {
        TextMatch match = FindInText.Next("port 9222 open", "9333", 0);

        Assert.False(match.Found);
        Assert.Equal(-1, match.Index);
        Assert.Equal(0, match.Total);
    }

    [Fact]
    public void AnEmptyNeedleMatchesNothing()
    {
        // Every offset is a match for the empty string, so a search box being cleared would
        // otherwise select a zero-width span and report a count as long as the file.
        Assert.False(FindInText.Next("anything", "", 0).Found);
        Assert.Equal(0, FindInText.Count("anything", ""));
    }

    [Fact]
    public void ANeedleLongerThanTheFileIsNotFound()
    {
        Assert.False(FindInText.Next("ab", "abcdef", 0).Found);
    }

    [Fact]
    public void ACaretPastTheEndIsClampedRatherThanThrowing()
    {
        // The caller hands over SelectionStart + SelectionLength, which lands past the end
        // whenever the last occurrence touches it. Throwing there would turn the last press
        // of Enter into a crash.
        TextMatch match = FindInText.Next("9222", "9222", 999);

        Assert.True(match.Found);
        Assert.Equal(0, match.Index);
    }

    [Fact]
    public void ANegativeCaretIsClampedToo()
    {
        Assert.True(FindInText.Next("9222", "9222", -5).Found);
    }

    [Fact]
    public void OverlappingOccurrencesAreCountedTheWayEnterWalksThem()
    {
        // "aaaa" holds three overlapping "aa" but Enter only ever stops on two of them.
        // A count of three would be a number the box can never reach.
        Assert.Equal(2, FindInText.Count("aaaa", "aa"));
    }

    [Fact]
    public void TheCountIsTheWholeFileRatherThanWhatIsLeft()
    {
        // Reported beside the box while the caret is at the very end: the person wants to
        // know how many there are, not how many are still ahead of them.
        TextMatch match = FindInText.Next("9222 9222 9222", "9222", 14);

        Assert.Equal(3, match.Total);
    }
}
