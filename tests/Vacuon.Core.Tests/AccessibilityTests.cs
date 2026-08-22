using System.Text.RegularExpressions;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M10, the part that costs work rather than money.
/// <para>
/// This reads the XAML as text instead of loading it, because the test project has no WPF
/// reference and adding one would put a UI dependency next to a core that deliberately has
/// none. Reading the markup is enough for the property this guards: every interactive
/// control must carry a name a screen reader can announce.
/// </para>
/// <para>
/// The check exists because accessibility rots silently. Nothing fails, nothing looks
/// wrong, and the person who cannot see the screen is the only one who finds out.
/// </para>
/// </summary>
public class AccessibilityTests
{
    private static readonly string ViewsDirectory = FindViews();

    private static string FindViews()
    {
        // Walk up from the test binary to the repository root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Vacuon.App", "Views");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        return string.Empty;
    }

    public static TheoryData<string> Views()
    {
        var data = new TheoryData<string>();

        if (ViewsDirectory.Length == 0) return data;

        foreach (string file in Directory.GetFiles(ViewsDirectory, "*.xaml"))
            data.Add(Path.GetFileName(file));

        return data;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void EveryCheckBoxCanBeAnnounced(string view)
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDirectory, view));

        foreach (Match match in Regex.Matches(xaml, @"<CheckBox\b"))
        {
            (string opening, string body) = ElementAt(xaml, match.Index, "CheckBox");

            bool named = opening.Contains("AutomationProperties.Name", StringComparison.Ordinal)
                      || opening.Contains("Content=", StringComparison.Ordinal)
                      || body.TrimStart().StartsWith('<')
                      // Set from code-behind, which this text check cannot see.
                      || opening.Contains("x:Name=", StringComparison.Ordinal);

            Assert.True(named,
                $"{view}: a CheckBox has no Content and no AutomationProperties.Name, so a " +
                "screen reader announces it as an unnamed checkbox.");
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void NoButtonIsLabelledOnlyByAnIconGlyph(string view)
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDirectory, view));

        foreach (Match match in Regex.Matches(xaml, @"<Button\b"))
        {
            (string opening, _) = ElementAt(xaml, match.Index, "Button");

            // A button whose face comes from the icon font has no readable content: the
            // glyph is a private-use character that reads as nothing, or as garbage.
            if (!opening.Contains("Font.Icon", StringComparison.Ordinal)) continue;

            Assert.Contains("AutomationProperties.Name", opening, StringComparison.Ordinal);
        }
    }

    /// <summary>Returns the opening tag and the body of the element starting at an index.</summary>
    private static (string Opening, string Body) ElementAt(string xaml, int start, string tag)
    {
        int close = xaml.IndexOf('>', start);
        if (close < 0) return (xaml[start..], string.Empty);

        string opening = xaml[start..(close + 1)];
        if (opening.TrimEnd().EndsWith("/>", StringComparison.Ordinal)) return (opening, string.Empty);

        int end = xaml.IndexOf($"</{tag}>", close, StringComparison.Ordinal);
        return (opening, end > 0 ? xaml[(close + 1)..end] : string.Empty);
    }

    [Fact]
    public void TheViewsFolderWasActuallyFound()
    {
        // Without this, every theory above would pass by having nothing to check — the
        // quietest way for a guard to stop guarding.
        Assert.NotEqual(string.Empty, ViewsDirectory);
        Assert.NotEmpty(Directory.GetFiles(ViewsDirectory, "*.xaml"));
    }
}
