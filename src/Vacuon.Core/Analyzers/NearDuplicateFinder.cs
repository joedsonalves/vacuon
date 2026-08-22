using System.Runtime.Versioning;
using Vacuon.Core.Index;
using Vacuon.Core.Preview;

namespace Vacuon.Core.Analyzers;

/// <summary>One picture in a near-duplicate group.</summary>
public sealed record SimilarImage(
    int EntryIndex,
    string Path,
    long Bytes,
    ulong Hash,
    uint? Width,
    uint? Height)
{
    /// <summary>Pixels, when the shell knew the dimensions. Used to pick what to keep.</summary>
    public long? PixelCount => Width is null || Height is null ? null : (long)Width * Height;

    public string? ResolutionLabel => Height is null or 0 ? null : $"{Height}p";
}

/// <summary>
/// Pictures that look the same, with the one worth keeping named.
/// <para>
/// Unlike an exact-duplicate group, the files here are genuinely different — different
/// bytes, different sizes, often different formats. What they share is the image, so the
/// choice is not "which copy" but "which version", and the answer is almost always the one
/// with the most pixels.
/// </para>
/// </summary>
public sealed class SimilarGroup
{
    internal SimilarGroup(IReadOnlyList<SimilarImage> images, SimilarImage keeper)
    {
        Images = images;
        Keeper = keeper;

        var others = new List<SimilarImage>(images.Count - 1);
        long recoverable = 0;

        foreach (SimilarImage image in images)
        {
            if (ReferenceEquals(image, keeper)) continue;
            others.Add(image);
            recoverable += image.Bytes;
        }

        Others = others;
        RecoverableBytes = recoverable;
    }

    public IReadOnlyList<SimilarImage> Images { get; }

    /// <summary>The version that stays. Never in <see cref="Others"/>.</summary>
    public SimilarImage Keeper { get; }

    public IReadOnlyList<SimilarImage> Others { get; }

    /// <summary>What removing the other versions would free.</summary>
    public long RecoverableBytes { get; }

    /// <summary>
    /// Largest distance between the keeper and anything else in the group.
    /// <para>
    /// Shown because it is the group's own confidence: at 0 these are the same image
    /// re-encoded, and at 9 they merely look alike — and the user is the one who should
    /// decide whether that is close enough to delete something over.
    /// </para>
    /// </summary>
    public int Spread
    {
        get
        {
            int worst = 0;

            foreach (SimilarImage image in Others)
            {
                int distance = PerceptualHash.Distance(Keeper.Hash, image.Hash);
                if (distance > worst) worst = distance;
            }

            return worst;
        }
    }
}

public sealed record SimilarReport(
    IReadOnlyList<SimilarGroup> Groups,
    int ImagesFingerprinted,
    int ImagesSkipped,
    int ImagesBelowMinimum = 0)
{
    public long RecoverableBytes
    {
        get
        {
            long total = 0;
            foreach (SimilarGroup group in Groups) total += group.RecoverableBytes;
            return total;
        }
    }
}

public sealed record NearDuplicateOptions
{
    /// <summary>Hamming distance at or below which two pictures are grouped.</summary>
    public int Threshold { get; init; } = PerceptualHash.DefaultThreshold;

    /// <summary>Images smaller than this are ignored. Icons and sprites are not photos.</summary>
    public long MinimumBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// Thumbnail size asked of the shell.
    /// <para>
    /// 128 px. Smaller loses the detail the fingerprint is made of; larger costs decode time
    /// per image and changes nothing, because the picture is reduced to a 9×8 grid anyway.
    /// </para>
    /// </summary>
    public ThumbnailSize Size { get; init; } = ThumbnailSize.Large;
}

/// <summary>
/// Finds pictures that look alike — the same photo at two resolutions, or the same frame
/// re-encoded.
/// <para>
/// Decoding is the shell's job. The thumbnail provider hands back raw pixels, so this reads
/// no image format itself and inherits whatever Windows can open, including HEIC and RAW
/// where the codec is installed. It is also why the app links no imaging library.
/// </para>
/// <para>
/// <b>Only content thumbnails are fingerprinted.</b> A file the shell answered with a type
/// icon is skipped and counted, never hashed: every <c>.docx</c> shares an icon, and hashing
/// those would group thousands of unrelated files as "the same picture".
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NearDuplicateFinder
{
    public SimilarReport Find(VolumeIndex index,
                              NearDuplicateOptions? options = null,
                              IProgress<DuplicateProgress>? progress = null,
                              CancellationToken cancellationToken = default)
    {
        options ??= new NearDuplicateOptions();

        var candidates = new List<int>();
        int belowMinimum = 0;
        FileEntry[] entries = index.Entries;

        for (int i = 0; i < entries.Length; i++)
        {
            ref FileEntry entry = ref entries[i];

            if (!entry.IsInUse || entry.IsDirectory) continue;

            // Only what could carry a picture. Asking the shell for a thumbnail of every
            // file on a volume would decode millions of things that have no image in them.
            if (FileCategories.Of(index.GetName(i)) != FileCategories.Image) continue;

            // Counted, not just skipped: an image below the floor was never compared, and
            // a report that only says how many were fingerprinted reads as though the rest
            // had been examined and found unique.
            if (entry.LogicalSize < options.MinimumBytes)
            {
                belowMinimum++;
                continue;
            }

            candidates.Add(i);
        }

        progress?.Report(new DuplicateProgress(0, candidates.Count, 0));

        using var thumbnails = new ThumbnailProvider(cacheCapacity: 64);

        var fingerprints = new List<SimilarImage>(candidates.Count);
        int skipped = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int entry = candidates[i];
            string path = index.GetFullPath(entry);
            if (path.Length == 0) continue;

            ulong? hash = PerceptualHash.Compute(
                thumbnails.Get(path, options.Size, preferContent: true));

            if (hash is null)
            {
                // No content thumbnail: an icon, or a file the shell would not decode.
                skipped++;
                continue;
            }

            MediaInfo media = MediaProbe.Read(path);

            fingerprints.Add(new SimilarImage(
                entry, path, index.Entries[entry].LogicalSize, hash.Value,
                media.Width, media.Height));

            if ((i & 31) == 0)
                progress?.Report(new DuplicateProgress(i, candidates.Count, 0));
        }

        List<SimilarGroup> groups = Group(fingerprints, options.Threshold, cancellationToken);

        groups.Sort(static (a, b) => b.RecoverableBytes.CompareTo(a.RecoverableBytes));

        return new SimilarReport(groups, fingerprints.Count, skipped, belowMinimum);
    }

    /// <summary>
    /// Clusters fingerprints that are within <paramref name="threshold"/> of each other.
    /// <para>
    /// Quadratic, and deliberately so at this size: the candidates are the images on a
    /// volume that survived a minimum-size filter, which is thousands, not millions.
    /// A BK-tree would be the answer if that ever stops being true.
    /// </para>
    /// </summary>
    private static List<SimilarGroup> Group(List<SimilarImage> images, int threshold,
                                            CancellationToken cancellationToken)
    {
        var groups = new List<SimilarGroup>();
        var taken = new bool[images.Count];

        for (int i = 0; i < images.Count; i++)
        {
            if (taken[i]) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var cluster = new List<SimilarImage> { images[i] };
            taken[i] = true;

            for (int j = i + 1; j < images.Count; j++)
            {
                if (taken[j]) continue;

                if (PerceptualHash.Distance(images[i].Hash, images[j].Hash) > threshold) continue;

                cluster.Add(images[j]);
                taken[j] = true;
            }

            if (cluster.Count > 1) groups.Add(new SimilarGroup(cluster, Choose(cluster)));
        }

        return groups;
    }

    /// <summary>
    /// Picks the version to keep: most pixels, then most bytes.
    /// <para>
    /// Pixels first, not bytes. A 4K frame saved as a well-compressed JPEG can be smaller
    /// than a 720p PNG of the same thing, and keeping the bigger file would throw away the
    /// better picture — which is the exact mistake this feature exists to prevent.
    /// </para>
    /// </summary>
    internal static SimilarImage Choose(IReadOnlyList<SimilarImage> images)
    {
        SimilarImage best = images[0];

        for (int i = 1; i < images.Count; i++)
        {
            SimilarImage candidate = images[i];

            long? candidatePixels = candidate.PixelCount;
            long? bestPixels = best.PixelCount;

            if (candidatePixels is not null && bestPixels is not null)
            {
                if (candidatePixels > bestPixels) best = candidate;
                else if (candidatePixels == bestPixels && candidate.Bytes > best.Bytes) best = candidate;
                continue;
            }

            // Dimensions unknown for one of them: fall back to bytes, and prefer the one we
            // do know something about.
            if (candidatePixels is not null && bestPixels is null) best = candidate;
            else if (candidatePixels is null && bestPixels is null && candidate.Bytes > best.Bytes)
                best = candidate;
        }

        return best;
    }
}
