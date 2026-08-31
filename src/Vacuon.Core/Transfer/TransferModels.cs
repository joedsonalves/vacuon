namespace Vacuon.Core.Transfer;

/// <summary>
/// What an item weighs, both ways at once.
/// <para>
/// Bytes drive the bar; files drive the count beside it. Measuring them separately is how
/// the count came to divide files landed by items selected.
/// </para>
/// </summary>
public readonly record struct TransferMeasurement(long Bytes, int Files);

/// <summary>What a batch is for. The three verbs the file list offers.</summary>
public enum TransferKind
{
    Copy,
    Move,
    /// <summary>Permanent removal. The Recycle Bin is not a transfer — see <see cref="FileTransferService"/>.</summary>
    Delete,
}

public enum TransferPhase
{
    /// <summary>Measuring and deciding. Nothing has been touched.</summary>
    Preparing,
    Running,
    Finished,
    /// <summary>Stopped on request. Whatever had already arrived stayed there.</summary>
    Cancelled,
    Failed,
}

public enum TransferOutcome
{
    Done,
    /// <summary>Refused by <see cref="Safety.ProtectedPaths"/>. Never attempted.</summary>
    Blocked,
    NotFound,
    /// <summary>A folder cannot be copied into itself or into anything below it.</summary>
    IntoItself,
    /// <summary>The destination is the folder the item is already in.</summary>
    AlreadyThere,
    Cancelled,
    Failed,
}

/// <summary>
/// One item's leg of the journey. <see cref="Destination"/> is where it is meant to end up,
/// which is not always <c>folder\originalName</c>: nothing is ever overwritten, so a name the
/// destination already uses arrives as <c>name (2)</c>.
/// </summary>
public sealed record TransferItem(
    string Source,
    string Destination,
    long Bytes,
    bool IsDirectory,
    TransferOutcome Refusal = TransferOutcome.Done,
    string? RefusalMessage = null)
{
    /// <summary>
    /// How many files this item stands for: one for a file, the whole subtree for a folder.
    /// <para>
    /// ⚠️ Not the same number as the item count, and the difference is not cosmetic. Progress
    /// counts files as the tool reports them landing, so a batch of a thousand files plus one
    /// folder holding five hundred more read "1,501 / 1,001" — a fraction whose halves were
    /// counting different things. The denominator has to be files too.
    /// </para>
    /// </summary>
    public int Files { get; init; } = 1;

    public bool IsRefused => Refusal != TransferOutcome.Done;

    public string Name => Path.GetFileName(Source.TrimEnd('\\'));

    public string FinalName => Path.GetFileName(Destination.TrimEnd('\\'));

    /// <summary>True when the destination already held that name and this one goes in under another.</summary>
    public bool Renamed => !IsRefused
        && !string.Equals(Name, FinalName, StringComparison.OrdinalIgnoreCase);
}

public sealed record TransferItemResult(
    TransferItem Item,
    TransferOutcome Outcome,
    long BytesTransferred,
    string? Message = null)
{
    public bool Succeeded => Outcome == TransferOutcome.Done;

    /// <summary>
    /// The files inside this item that could not be copied, named one by one.
    /// <para>
    /// A folder is one item, so a folder whose contents partly failed reported a single line
    /// saying something had gone wrong, with no way to find out what. These are the paths the
    /// tool itself named while it worked, with the retries folded together.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> FailedPaths { get; init; } = [];
}

/// <summary>
/// A batch worked out in full before anything is touched.
/// <para>
/// <see cref="FileTransferService.ExecuteAsync"/> takes one of these rather than a list of
/// paths, so the run can only act on what the confirmation showed. A file that appeared
/// between the plan and the run is not in the plan and does not travel — the same structural
/// dry-run the cleanup engine has.
/// </para>
/// </summary>
public sealed record TransferPlan(
    TransferKind Kind,
    string Destination,
    IReadOnlyList<TransferItem> Items)
{
    public IEnumerable<TransferItem> Movable => Items.Where(i => !i.IsRefused);
    public IEnumerable<TransferItem> Refused => Items.Where(i => i.IsRefused);
    public IEnumerable<TransferItem> Renames => Items.Where(i => i.Renamed);

    /// <summary>Items the batch will act on — a folder counts once, however deep it is.</summary>
    public int Count => Movable.Count();

    /// <summary>
    /// Files the batch will actually write or remove, folders counted through. This is what
    /// a "so many of so many" readout has to divide by; <see cref="Count"/> is what a
    /// confirmation dialog lists.
    /// </summary>
    public int FileCount => Movable.Sum(i => i.Files);

    /// <summary>
    /// Bytes the plan expects to move, as measured before the run — never robocopy's word
    /// for it, and never the figure reported as transferred afterwards.
    /// </summary>
    public long Bytes => Movable.Sum(i => i.Bytes);

    public bool IsEmpty => !Movable.Any();
}

/// <summary>
/// A snapshot of a running batch, cheap enough to raise several times a second.
/// <para>
/// <see cref="Remaining"/> is deliberately nullable. An estimate needs both a total and a
/// rate, and for the first moments of a run there is no rate — showing "0 seconds left" or
/// freezing on a stale guess would both be the app stating something it has not measured.
/// Null means "not knowable yet", and the window says exactly that.
/// </para>
/// </summary>
public sealed record TransferProgress(
    TransferPhase Phase,
    string CurrentItem,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    double FilesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    int? CurrentFilePercent = null)
{
    /// <summary>
    /// Fraction of the planned bytes already through, or null when the plan carried no
    /// total to divide by — an empty folder tree weighs nothing and cannot be a percentage.
    /// </summary>
    public double? Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : null;
}

/// <summary>
/// What a finished batch actually did.
/// <para>
/// <see cref="BytesTransferred"/> is counted from the per-file lines the tool emitted and
/// then checked against the total it printed in its own summary. When the two disagree,
/// <see cref="TotalIsUncertain"/> is set and nothing quotes a single confident figure: two
/// numbers derived from the same reality are compared by the app, not by the person.
/// </para>
/// </summary>
public sealed record TransferReport(
    TransferKind Kind,
    string Destination,
    IReadOnlyList<TransferItemResult> Results,
    TransferPhase Phase,
    long BytesTransferred,
    bool TotalIsUncertain,
    TimeSpan Elapsed,
    string? Message = null)
{
    public int DoneCount => Results.Count(r => r.Succeeded);
    public int FailedCount => Results.Count(r => r.Outcome is not (TransferOutcome.Done or TransferOutcome.AlreadyThere));

    public IEnumerable<TransferItemResult> Failures =>
        Results.Where(r => r.Outcome is not (TransferOutcome.Done or TransferOutcome.AlreadyThere));

    public IEnumerable<TransferItemResult> Blocked =>
        Results.Where(r => r.Outcome == TransferOutcome.Blocked);

    public bool WasCancelled => Phase == TransferPhase.Cancelled;

    /// <summary>Every file named as failed, across all the items in the batch.</summary>
    public IReadOnlyList<string> FailedFilePaths => [.. Results.SelectMany(r => r.FailedPaths)];

    /// <summary>
    /// How many files the tool's own closing table counted as FAILED.
    /// <para>
    /// A second reading of the same reality as <see cref="FailedFilePaths"/>, and the window
    /// compares the two rather than leaving it to the person: an error line names one file,
    /// but a single error can sink a whole directory, so the named list can be shorter than
    /// this count. When it is, the window says the list is partial instead of implying it is
    /// everything.
    /// </para>
    /// </summary>
    public int FailedFileCount { get; init; }

    /// <summary>
    /// Whether these bytes are space the source volume got back.
    /// <para>
    /// A copy frees nothing anywhere, and a move inside one volume rewrites a directory
    /// entry. Only a permanent delete, or a move that left the volume, returns space — and
    /// this is the only property that says so, so nothing can quote "freed" by accident.
    /// </para>
    /// </summary>
    public bool BytesWereFreed { get; init; }
}
