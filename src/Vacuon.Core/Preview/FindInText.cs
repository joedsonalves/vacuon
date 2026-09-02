namespace Vacuon.Core.Preview;

/// <summary>Where the next occurrence is, and how many there are in all.</summary>
/// <param name="Index">Character offset of the match, or -1 when there is none.</param>
/// <param name="Length">Length of the match, so the caller can select it without measuring.</param>
/// <param name="Total">How many occurrences the whole text holds.</param>
public readonly record struct TextMatch(int Index, int Length, int Total)
{
    public bool Found => Index >= 0;

    public static TextMatch None { get; } = new(-1, 0, 0);
}

/// <summary>
/// Finding the next occurrence inside the file being edited.
/// <para>
/// In the core rather than beside the box it moves the caret in, because two screens ask
/// this same question — the preview pane and the editor window — and the answer must not
/// depend on which one asked. It also makes the wrap-around and the count testable, which
/// they were not while they lived in two copies of a code-behind.
/// </para>
/// <para>
/// ⚠️ Not called <c>TextSearch</c>: WPF already has a <c>System.Windows.Controls.TextSearch</c>
/// and the two collide in every view file that would use this one. The compiler catches it,
/// but only after somebody has written the file.
/// </para>
/// </summary>
public static class FindInText
{
    /// <summary>
    /// The first occurrence at or after <paramref name="from"/>, wrapping to the start.
    /// </summary>
    /// <remarks>
    /// It wraps rather than stopping at the end, and <see cref="TextMatch.Total"/> is what
    /// keeps that from being a surprise: somebody who can see there are three occurrences
    /// knows what the fourth press of Enter did.
    /// <para>
    /// Case-insensitive, deliberately. Searching a file for <c>9222</c> and being told there
    /// is no such thing because the file says <c>0x922B</c> in another case is the kind of
    /// answer that is true and useless.
    /// </para>
    /// </remarks>
    public static TextMatch Next(string haystack, string needle, int from)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);

        if (needle.Length == 0 || needle.Length > haystack.Length) return TextMatch.None;

        int start = Math.Clamp(from, 0, haystack.Length);

        int at = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);

        // Past the end is not "not found": it means the search has to come back around.
        if (at < 0) at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        return at < 0 ? TextMatch.None : new TextMatch(at, needle.Length, Count(haystack, needle));
    }

    /// <summary>
    /// How many times the needle appears, counting non-overlapping occurrences.
    /// </summary>
    /// <remarks>
    /// Non-overlapping because that is what pressing Enter walks through: in
    /// <c>aaaa</c> the search for <c>aa</c> stops twice, so answering "3" would be a number
    /// the box could never reach.
    /// </remarks>
    public static int Count(string haystack, string needle)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);

        if (needle.Length == 0) return 0;

        int total = 0;

        for (int at = 0; (at = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0;)
        {
            total++;
            at += needle.Length;
        }

        return total;
    }
}
