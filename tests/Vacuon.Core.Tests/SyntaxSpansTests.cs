using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M3, F6.6 — colouring the text preview.
/// <para>
/// One shallow tokenizer serves every language, so most of what matters here is that it stays
/// inside its lane: it must never run a colour off the end of the text, never swallow the rest
/// of a file because of one stray quote, and never return overlapping runs that the view would
/// then have to reconcile.
/// </para>
/// </summary>
public class SyntaxSpansTests
{
    private static SyntaxSpan Single(string text, TokenKind kind)
    {
        SyntaxSpan span = Assert.Single(SyntaxSpans.Of(text));
        Assert.Equal(kind, span.Kind);
        return span;
    }

    private static string TextOf(string source, SyntaxSpan span) =>
        source.Substring(span.Start, span.Length);

    [Fact]
    public void ALineCommentIsColoured()
    {
        const string Source = "x = 1 // and the rest";
        IReadOnlyList<SyntaxSpan> spans = SyntaxSpans.Of(Source);

        SyntaxSpan comment = Assert.Single(spans, s => s.Kind == TokenKind.Comment);
        Assert.Equal("// and the rest", TextOf(Source, comment));
    }

    [Theory]
    [InlineData("# a python comment")]
    [InlineData("-- a sql comment")]
    [InlineData("// a c comment")]
    public void EveryLineCommentSyntaxIsRecognised(string source)
    {
        Assert.Contains(SyntaxSpans.Of(source), s => s.Kind == TokenKind.Comment);
    }

    [Fact]
    public void ACommentStopsAtTheEndOfItsLine()
    {
        const string Source = "// one\nkeep";
        SyntaxSpan comment = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.Comment);

        Assert.Equal("// one", TextOf(Source, comment));
    }

    [Fact]
    public void ABlockCommentSpansLines()
    {
        const string Source = "a /* two\nlines */ b";
        SyntaxSpan comment = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.Comment);

        Assert.Equal("/* two\nlines */", TextOf(Source, comment));
    }

    [Fact]
    public void AnUnclosedBlockCommentEndsWithTheText()
    {
        // Not an exception, and not a length that runs past the end: the preview is a
        // truncated 64 KiB read, so an unterminated anything is the normal case here.
        const string Source = "a /* never closed";
        SyntaxSpan comment = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.Comment);

        Assert.Equal(Source.Length, comment.End);
    }

    [Fact]
    public void AStringIsColoured()
    {
        const string Source = "name = \"vacuon\"";
        SyntaxSpan text = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.String);

        Assert.Equal("\"vacuon\"", TextOf(Source, text));
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheString()
    {
        const string Source = "s = \"a \\\" b\" end";
        SyntaxSpan text = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.String);

        Assert.Equal("\"a \\\" b\"", TextOf(Source, text));
    }

    [Fact]
    public void AnUnterminatedStringStopsAtTheLineBreak()
    {
        // Otherwise one stray quote paints the remaining 64 kilobytes as a string, and the
        // preview looks broken rather than useful.
        const string Source = "s = \"oops\nreal code here";
        SyntaxSpan text = Assert.Single(SyntaxSpans.Of(Source), s => s.Kind == TokenKind.String);

        Assert.DoesNotContain('\n', TextOf(Source, text));
    }

    [Fact]
    public void AKeywordIsColoured()
    {
        Assert.Contains(SyntaxSpans.Of("public class Thing"),
                        s => s.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void AWordThatMerelyContainsAKeywordIsNot()
    {
        // "classical" is not "class". Without a word boundary the colouring turns into noise.
        Assert.DoesNotContain(SyntaxSpans.Of("classical music"),
                              s => s.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void ANumberIsColoured()
    {
        SyntaxSpan number = Single("42", TokenKind.Number);
        Assert.Equal("42", TextOf("42", number));
    }

    [Fact]
    public void DigitsInsideAnIdentifierAreNotANumber()
    {
        Assert.DoesNotContain(SyntaxSpans.Of("value2 = 3"),
                              s => s.Kind == TokenKind.Number && s.Start == 5);
    }

    [Fact]
    public void SpansNeverOverlapAndNeverGoBackwards()
    {
        // The view walks them in order and takes each Start as the end of the previous plain
        // run. Overlapping or unsorted spans would silently produce garbled text.
        const string Source = """
            // a comment
            public class Thing { const int N = 42; string s = "hi"; /* block */ }
            # python-ish
            """;

        IReadOnlyList<SyntaxSpan> spans = SyntaxSpans.Of(Source);

        Assert.NotEmpty(spans);

        for (int i = 1; i < spans.Count; i++)
            Assert.True(spans[i].Start >= spans[i - 1].End,
                $"span {i} starts at {spans[i].Start}, inside the one ending at {spans[i - 1].End}");
    }

    [Fact]
    public void EverySpanStaysInsideTheText()
    {
        const string Source = "const x = \"unterminated /* and a comment start";

        foreach (SyntaxSpan span in SyntaxSpans.Of(Source))
        {
            Assert.True(span.Start >= 0);
            Assert.True(span.End <= Source.Length, $"span ends at {span.End}, past {Source.Length}");
        }
    }

    [Fact]
    public void EmptyTextProducesNothing()
    {
        Assert.Empty(SyntaxSpans.Of(string.Empty));
    }

    [Theory]
    [InlineData("Program.cs", true)]
    [InlineData("app.tsx", true)]
    [InlineData("build.gradle", true)]
    [InlineData("notes.txt", false)]
    [InlineData("server.log", false)]
    [InlineData("export.csv", false)]
    [InlineData("noextension", false)]
    public void OnlySourceFilesAreColoured(string name, bool expected)
    {
        // A log has no syntax to reveal, and colouring arbitrary words in one invents
        // structure that is not there.
        Assert.Equal(expected, SyntaxSpans.IsSource(name));
    }
}
