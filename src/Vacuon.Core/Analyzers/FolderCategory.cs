using Vacuon.Core.Index;

namespace Vacuon.Core.Analyzers;

/// <summary>
/// Which category takes the most space inside a folder.
/// <para>
/// A treemap coloured by file type goes flat grey the moment it draws folders, because a
/// folder has no extension — and the top level of a volume is almost entirely folders, so
/// the colour says nothing exactly where the map is most useful. Charging a folder with the
/// category that occupies most of it turns the top level back into information: this branch
/// is video, that one is build output.
/// </para>
/// </summary>
public static class FolderCategory
{
    /// <summary>
    /// Walks a subtree and returns the category holding the most bytes, or
    /// <see cref="FileCategories.Other"/> for a folder with nothing in it.
    /// </summary>
    public static string Dominant(VolumeIndex index, int folder, CancellationToken cancellationToken = default)
    {
        var totals = new Dictionary<string, long>(16, StringComparer.Ordinal);

        // Explicit stack: a deep tree would blow the real one, and this runs over whole
        // volumes where 40 levels is ordinary and 200 is not impossible.
        var stack = new Stack<int>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int current = stack.Pop();

            foreach (int child in index.GetChildren(current))
            {
                ref FileEntry entry = ref index.Entries[child];
                if (!entry.IsInUse) continue;

                if (entry.IsDirectory)
                {
                    stack.Push(child);
                    continue;
                }

                long bytes = entry.AllocatedSize;
                if (bytes <= 0) continue;

                string category = FileCategories.Of(index.GetName(child));
                totals.TryGetValue(category, out long running);
                totals[category] = running + bytes;
            }
        }

        string best = FileCategories.Other;
        long most = 0;

        foreach ((string category, long bytes) in totals)
        {
            if (bytes <= most) continue;
            most = bytes;
            best = category;
        }

        return best;
    }
}
