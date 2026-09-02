using System.Runtime.Versioning;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Preview;

/// <summary>An edit that could not be written yet, waiting for whoever holds the file to let go.</summary>
public sealed record PendingSave(string Path, byte[] Content, DateTime QueuedUtc)
{
    public long Bytes => Content.LongLength;
}

/// <summary>What happened when a pending save was finally attempted.</summary>
public sealed record PendingSaveResult(PendingSave Save, SaveOutcome Outcome, string? Message);

/// <summary>
/// Holds edits that a locked file refused, and writes them the moment it is released.
/// <para>
/// The situation this exists for: somebody edits a config file while the program that owns it
/// is running, presses save, and is told the file is in use. Telling them to close the program
/// and do the work again is the app handing back a problem it is in a better position to
/// solve — it already knows the path, it already has the bytes, and it can watch.
/// </para>
/// <para>
/// ⚠️ <b>Nothing is written without the file being free.</b> The check is an actual open for
/// writing with no sharing, not a guess from the holder list: a program can hold a file and
/// still share it for writing, and <see cref="RestartManager"/> would name it either way.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PendingSaves
{
    private readonly Dictionary<string, PendingSave> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Raised on the polling thread when a waiting edit is written, or gives up.</summary>
    public event EventHandler<PendingSaveResult>? Settled;

    public int Count
    {
        get { lock (_gate) return _waiting.Count; }
    }

    public IReadOnlyList<PendingSave> Waiting
    {
        get { lock (_gate) return [.. _waiting.Values]; }
    }

    /// <summary>
    /// Remembers an edit to write later.
    /// </summary>
    /// <remarks>
    /// Queueing the same path twice keeps the newer content: the person edited again, and the
    /// older bytes are a version they already moved past.
    /// </remarks>
    public PendingSave Queue(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var save = new PendingSave(path, content, DateTime.UtcNow);

        lock (_gate) _waiting[path] = save;

        return save;
    }

    public bool Forget(string path)
    {
        lock (_gate) return _waiting.Remove(path);
    }

    public void Clear()
    {
        lock (_gate) _waiting.Clear();
    }

    /// <summary>
    /// Tries every waiting edit once, writing the ones whose file is now free.
    /// </summary>
    /// <returns>How many were written.</returns>
    public int TryAll()
    {
        PendingSave[] pending;
        lock (_gate) pending = [.. _waiting.Values];

        int written = 0;

        foreach (PendingSave save in pending)
        {
            // ⚠️ The permanent refusals are decided BEFORE the writability check, and that
            // order is the whole correctness of this loop. A protected path and a file that
            // no longer exists both fail `CanWrite`, so testing writability first would leave
            // them queued for ever, retried every few seconds against something that is never
            // going to change.
            if (Safety.ProtectedPaths.IsProtected(save.Path))
            {
                Settle(save, SaveOutcome.Protected, null);
                continue;
            }

            if (!File.Exists(save.Path))
            {
                // The file went away while the edit waited. Writing the bytes now would
                // bring back something somebody deleted, under a name they expected to be
                // gone — worse than losing the edit, which is what the message says happened.
                Settle(save, SaveOutcome.Failed, "gone");
                continue;
            }

            if (!FileAvailability.CanWrite(save.Path)) continue;

            SaveResult result = FileEditor.SaveBytes(save.Path, save.Content);

            // Still locked between the check and the write: somebody else got there first,
            // and it is worth waiting for the next round rather than giving up.
            if (result.Outcome == SaveOutcome.InUse) continue;

            Settle(save, result.Outcome, result.Message);

            if (result.Succeeded) written++;
        }

        return written;
    }

    private void Settle(PendingSave save, SaveOutcome outcome, string? message)
    {
        lock (_gate) _waiting.Remove(save.Path);

        Settled?.Invoke(this, new PendingSaveResult(save, outcome, message));
    }

    /// <summary>
    /// Keeps trying until everything is written or the wait is called off.
    /// </summary>
    /// <param name="interval">
    /// How often to look. Seconds, not milliseconds: this is waiting for a person to close a
    /// program, and polling a locked file hard costs a handle open every time.
    /// </param>
    public async Task WatchAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Count > 0)
        {
            TryAll();

            if (Count == 0) return;

            try { await Task.Delay(interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Whether a file can be written to right now.</summary>
[SupportedOSPlatform("windows")]
public static class FileAvailability
{
    /// <summary>
    /// Opens the file for writing with no sharing, and closes it again.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An actual open, not a guess.</b> Asking the Restart Manager who holds a file
    /// answers a different question: a program can hold a file open and still share it for
    /// writing, and one that shares it for reading only will refuse this. The only reliable
    /// way to know whether a write will work is to ask for the same access the write needs.
    /// </remarks>
    public static bool CanWrite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The programs holding the file, for saying who to close.</summary>
    public static IReadOnlyList<FileHolder> Holders(string path)
    {
        try { return RestartManager.WhoHolds(path); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }
}
