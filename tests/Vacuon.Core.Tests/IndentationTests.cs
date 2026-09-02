using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Reading a file's own indentation off the file, so the editor inserts lines that look like
/// the ones around them.
/// </summary>
public class IndentationTests
{
    [Fact]
    public void TabsWin()
    {
        Assert.Equal("\t", Indentation.Detect("classe\n\tmetodo\n\t\tlinha\n"));
    }

    [Fact]
    public void TwoSpacesAreFound()
    {
        Assert.Equal("  ", Indentation.Detect("a\n  b\n    c\n  d\n"));
    }

    [Fact]
    public void FourSpacesAreFound()
    {
        Assert.Equal("    ", Indentation.Detect("a\n    b\n        c\n"));
    }

    [Fact]
    public void TheStepIsTheDifference()
    {
        // ⚠️ The deepest indent is eight, and the file steps by two. Reading the smallest
        // indent found would have said eight for the second line and been wrong about the
        // whole file.
        Assert.Equal("  ", Indentation.Detect("raiz\n  um\n    dois\n      tres\n        quatro\n"));
    }

    [Fact]
    public void ABlankLineOfSpacesSaysNothing()
    {
        // Trailing whitespace on an otherwise empty line is not nesting, and treating it as
        // a one-space step would poison the answer for the whole file.
        Assert.Equal("    ", Indentation.Detect("a\n \n    b\n        c\n"));
    }

    [Fact]
    public void AFileWithNoIndentGetsTheConvention()
    {
        Assert.Equal(Indentation.Default, Indentation.Detect("uma linha\noutra linha\n"));
    }

    [Fact]
    public void AnEmptyFileGetsTheConvention()
    {
        Assert.Equal(Indentation.Default, Indentation.Detect(string.Empty));
    }

    [Fact]
    public void TheCurrentLineIndentIsCopied()
    {
        const string text = "raiz\r\n    dentro\r\nfora\r\n";
        int caret = text.IndexOf("dentro", StringComparison.Ordinal) + 3;

        Assert.Equal("    ", Indentation.LeadingWhitespaceAt(text, caret));
    }

    [Fact]
    public void NothingIsCopiedFromBeyondTheCaret()
    {
        // The caret sits in the middle of the leading spaces. Copying all eight would insert
        // whitespace from a place the person is not standing in.
        const string text = "a\r\n        b\r\n";
        int caret = text.IndexOf('\n') + 1 + 3;

        Assert.Equal("   ", Indentation.LeadingWhitespaceAt(text, caret));
    }

    [Fact]
    public void TheFirstLineWorksToo()
    {
        Assert.Equal("\t", Indentation.LeadingWhitespaceAt("\tprimeira\r\nsegunda", 5));
    }

    [Fact]
    public void ACaretPastTheEndDoesNotThrow()
    {
        Assert.Equal(string.Empty, Indentation.LeadingWhitespaceAt("abc", 9999));
    }
}
