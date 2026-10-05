using Vacuon.Core.Index;
using Vacuon.Core.Safety;

namespace Vacuon.Core.Analyzers;

/// <summary>
/// The folders on a volume where files wait to be removed rather than live: the Recycle Bin,
/// and this app's quarantine.
/// <para>
/// ⚠️ <b>A copy in there is not a copy to keep.</b> Every finder that groups files by content
/// picks one of each group to stay — the oldest by default, or the one on the shallowest path
/// — and offers the rest for removal. A file set aside in the quarantine keeps its dates, and
/// <c>C:\$Vacuon.Quarantine\…</c> or <c>C:\$Recycle.Bin\…</c> is about as shallow as a path
/// gets, so the copy chosen to stay could be the one already on its way out, with the real
/// file offered for removal beside it. Empty the bin or purge the quarantine afterwards, and
/// both are gone. The protection list already refuses to let anything delete in here; this is
/// the other half, so nothing in here is offered as the copy that stays either.
/// </para>
/// <para>
/// Measured on my C: on 5 October 2026: 46 files and 5,057 bytes in the bin, an empty
/// quarantine, so no group there was affected that day. The hole is there whenever either
/// one holds something bigger than a few kilobytes.
/// </para>
/// </summary>
public sealed class HoldingAreas
{
    /// <summary>Names of the holding folders, at the root of every volume.</summary>
    private static readonly string[] Names = ["$Recycle.Bin", ProtectedPaths.QuarantineFolderName];

    private readonly HashSet<int> _inside;

    private HoldingAreas(HashSet<int> inside) => _inside = inside;

    /// <summary>The holding areas of the volume <paramref name="index"/> covers, and everything in them.</summary>
    public static HoldingAreas Of(VolumeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var inside = new HashSet<int>();
        var pending = new Stack<int>();

        foreach (string name in Names)
        {
            int root = index.FindEntry(Path.Combine(index.Volume.Root, name));
            if (root < 0 || !index.Entries[root].IsDirectory) continue;

            pending.Push(root);

            while (pending.Count > 0)
            {
                int current = pending.Pop();
                if (!inside.Add(current)) continue;

                foreach (int child in index.GetChildren(current))
                    if (index.Entries[child].IsInUse) pending.Push(child);
            }
        }

        return new HoldingAreas(inside);
    }

    /// <summary>True when the entry is one of the holding folders or anything below them.</summary>
    public bool Contains(int entry) => _inside.Contains(entry);
}
