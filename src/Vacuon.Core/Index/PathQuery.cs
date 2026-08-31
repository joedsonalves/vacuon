namespace Vacuon.Core.Index;

public enum PathQueryOutcome
{
    /// <summary>Ordinary search text. Nothing about it claimed to be a path.</summary>
    NotAPath,
    /// <summary>A path, but on a drive this scan is not of.</summary>
    OtherVolume,
    /// <summary>Unmistakably a path, and this index has never heard of it.</summary>
    NotFound,
    Folder,
    File,
}

public readonly record struct PathQueryResult(PathQueryOutcome Outcome, int EntryIndex, string Path)
{
    public bool Resolved => Outcome is PathQueryOutcome.Folder or PathQueryOutcome.File;
}

/// <summary>
/// Reads a folder path out of the search box.
/// <para>
/// Pasting <c>C:\Users\me\Videos\renders</c> into a search field and being shown every file
/// whose <em>name</em> contains that string — which is none of them — is the failure this
/// removes. A path in the box means "show me what is in here", answered from the scan
/// already in memory, with no trip to the disk.
/// </para>
/// <para>
/// Two rules keep it from guessing. It only reports <see cref="PathQueryOutcome.NotFound"/>
/// for text that could not be anything else: a drive letter or a leading separator. Anything
/// vaguer that fails to resolve comes back <see cref="PathQueryOutcome.NotAPath"/>, and the
/// caller falls through to searching by name — so typing a word with a backslash in it never
/// costs you the ordinary search. And a path on another drive is told apart from a path that
/// does not exist, because "there is no such folder" would be false and misleading when the
/// folder is simply on a volume nobody scanned.
/// </para>
/// </summary>
public static class PathQuery
{
    /// <summary>
    /// Tidies what a person is likely to paste: surrounding spaces, the quotes Explorer's
    /// "Copy as path" wraps around everything, and forward slashes, which every shell and
    /// half the internet will hand you for a Windows path.
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string trimmed = text.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Trim();

        return trimmed.Replace('/', '\\');
    }

    /// <summary>True when the text can only have been meant as a path.</summary>
    public static bool IsExplicit(string cleaned) =>
        cleaned.Length >= 2 && ((char.IsLetter(cleaned[0]) && cleaned[1] == ':') || cleaned[0] == '\\');

    public static PathQueryResult Resolve(string text, VolumeIndex index)
    {
        string cleaned = Clean(text);
        if (cleaned.Length == 0) return new PathQueryResult(PathQueryOutcome.NotAPath, -1, string.Empty);

        bool wasExplicit = IsExplicit(cleaned);

        // A bare "D:" means that drive's root, which is what people type when they mean it.
        if (cleaned.Length == 2 && cleaned[1] == ':') cleaned += '\\';

        if (cleaned.Length >= 2 && cleaned[1] == ':')
        {
            if (char.ToUpperInvariant(cleaned[0]) != char.ToUpperInvariant(index.Volume.DriveLetter))
                return new PathQueryResult(PathQueryOutcome.OtherVolume, -1, cleaned);
        }
        else if (cleaned[0] == '\\')
        {
            // A UNC share is not this volume and never will be — the index is one local disk.
            if (cleaned.StartsWith(@"\\", StringComparison.Ordinal))
                return new PathQueryResult(PathQueryOutcome.OtherVolume, -1, cleaned);

            cleaned = index.Volume.Root.TrimEnd('\\') + cleaned;
        }
        else if (!cleaned.Contains('\\'))
        {
            // A single word with no separator is a name to search for, not a path.
            return new PathQueryResult(PathQueryOutcome.NotAPath, -1, cleaned);
        }
        else
        {
            // Relative-looking text such as "Users\me": read it from the volume root, which
            // is the only anchor the index has.
            cleaned = index.Volume.Root.TrimEnd('\\') + '\\' + cleaned.TrimStart('\\');
        }

        int entry = index.FindEntry(cleaned);

        if (entry < 0)
        {
            return wasExplicit
                ? new PathQueryResult(PathQueryOutcome.NotFound, -1, cleaned)
                : new PathQueryResult(PathQueryOutcome.NotAPath, -1, cleaned);
        }

        return new PathQueryResult(
            index.Entries[entry].IsDirectory ? PathQueryOutcome.Folder : PathQueryOutcome.File,
            entry,
            cleaned);
    }
}
