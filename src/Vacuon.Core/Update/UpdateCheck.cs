using System.Diagnostics;
using System.Globalization;

namespace Vacuon.Core.Update;

/// <summary>What the check managed to find out.</summary>
public enum UpdateOutcome
{
    /// <summary>Nothing newer is on offer.</summary>
    UpToDate,
    /// <summary>A newer version is available from the package source.</summary>
    Available,
    /// <summary>winget is not on this machine, so there is nothing to ask.</summary>
    NoWinget,
    /// <summary>winget is here, but this app was not installed through it.</summary>
    NotInstalledByWinget,
    /// <summary>The command ran and its answer could not be read.</summary>
    Unreadable,
}

/// <summary>
/// Three versions of the same app, compared by the app rather than by the person.
/// </summary>
/// <param name="Running">The build that is executing right now — <see cref="AppInfo.Version"/>.</param>
/// <param name="Installed">What winget believes is installed, or null when it has no opinion.</param>
/// <param name="Available">What the package source offers, or null when it offers nothing newer.</param>
public readonly record struct UpdateStatus(
    UpdateOutcome Outcome,
    string Running,
    string? Installed,
    string? Available)
{
    /// <summary>
    /// True when the running build is ahead of what the source has — which is not an error,
    /// and happens on purpose every time a release goes out before its manifest is merged.
    /// </summary>
    public bool RunningIsAhead =>
        Installed is not null && Compare(Running, Installed) > 0;

    /// <summary>Ordering two version strings by their numeric parts, missing parts as zero.</summary>
    public static int Compare(string? left, string? right)
    {
        if (left is null || right is null) return 0;

        string[] a = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] b = right.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            long x = i < a.Length && long.TryParse(a[i], NumberStyles.Integer,
                                                   CultureInfo.InvariantCulture, out long pa) ? pa : 0;
            long y = i < b.Length && long.TryParse(b[i], NumberStyles.Integer,
                                                   CultureInfo.InvariantCulture, out long pb) ? pb : 0;

            if (x != y) return x < y ? -1 : 1;
        }

        return 0;
    }
}

/// <summary>
/// Asks winget whether there is a newer Vacuon.
/// <para>
/// ⚠️ <b>The app opens no socket of its own.</b> It runs the package manager already on the
/// machine and reads what that prints. Everything Vacuon does is local, and a version check
/// that quietly started talking to a server would be the first thing to break that — so the
/// network trip belongs to winget, which the person installed on purpose and can point
/// wherever they like.
/// </para>
/// <para>
/// ⚠️ <b>What this can and cannot tell you.</b> It reports the winget <em>source</em>, which
/// lags a release: a version can be published, tagged and downloadable while the manifest is
/// still in review, and during that window winget answers "nothing newer" perfectly honestly.
/// That is why the running build is carried alongside and compared — "you are on 0.6.0 and
/// the source still offers 0.5.0" is the truth, and pretending the source is the last word
/// would make the app tell somebody they are behind when they are ahead.
/// </para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>The identifier the manifests are published under.</summary>
    public const string PackageId = "Joedsonalves.Vacuon";

    /// <summary>Longer than this and the answer is not worth the wait.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static async Task<UpdateStatus> QueryAsync(CancellationToken cancellationToken = default)
    {
        string running = AppInfo.Version;

        // Both questions in one pass: what is installed, and what is on offer. The upgrade
        // listing alone cannot answer the first — a package that is up to date is simply
        // absent from it.
        string? listed = await RunAsync(["list", "--id", PackageId, "--exact", "--disable-interactivity"],
                                        cancellationToken).ConfigureAwait(false);

        if (listed is null) return new UpdateStatus(UpdateOutcome.NoWinget, running, null, null);

        string[] installedFields = FieldsAfterId(listed);
        string? installed = installedFields.Length > 0 ? installedFields[0] : null;

        if (installed is null) return new UpdateStatus(UpdateOutcome.NotInstalledByWinget, running, null, null);

        string? upgradable = await RunAsync(
            ["upgrade", "--id", PackageId, "--exact", "--include-unknown", "--disable-interactivity"],
            cancellationToken).ConfigureAwait(false);

        string? available = upgradable is null ? null : AvailableIn(upgradable);

        UpdateOutcome outcome = available is null
            ? UpdateOutcome.UpToDate
            : UpdateStatus.Compare(available, running) > 0
                ? UpdateOutcome.Available
                : UpdateOutcome.UpToDate;

        return new UpdateStatus(outcome, running, installed, available);
    }

    /// <summary>
    /// The version column of an upgrade row, or null when the output holds no such row.
    /// <para>
    /// Read by position relative to the package id, never by column header: the headers are
    /// translated — this machine prints <c>Nome / ID / Versão / Disponível / Origem</c> —
    /// while the id is a literal that no locale touches. A listing row carries the version
    /// and the source after the id; an upgrade row carries the version, the one on offer and
    /// then the source. Three fields or more, with the middle one starting in a digit, is
    /// what makes it an offer.
    /// </para>
    /// </summary>
    public static string? AvailableIn(string output)
    {
        string[] fields = FieldsAfterId(output);

        if (fields.Length < 3) return null;
        if (fields[1].Length == 0 || !char.IsDigit(fields[1][0])) return null;

        return fields[1];
    }

    /// <summary>Whitespace-separated fields following the package id, on the line that holds it.</summary>
    public static string[] FieldsAfterId(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            int at = trimmed.IndexOf(PackageId, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            // The name column can hold anything, version numbers included ("AdsPower Global
            // 7.12.29"), so only what comes after the id is read.
            string tail = trimmed[(at + PackageId.Length)..];

            string[] fields = tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0) return fields;
        }

        return [];
    }

    /// <summary>Runs winget and returns its output, or null when winget is not there.</summary>
    private static async Task<string?> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return null;

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(Timeout);

            Task<string> output = process.StandardOutput.ReadToEndAsync(window.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(window.Token);

            await process.WaitForExitAsync(window.Token).ConfigureAwait(false);

            // Its exit code is not read on purpose: "no applicable update found" is a
            // failure code for a perfectly good answer, and this reads the answer.
            return await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException
                                        or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The command that performs the update, shown to the person rather than run behind them.
    /// </summary>
    public static string UpgradeCommand => $"winget upgrade --id {PackageId} --exact";
}
