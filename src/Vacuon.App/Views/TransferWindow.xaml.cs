using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;
using Vacuon.Core.Transfer;

namespace Vacuon.App.Views;

/// <summary>
/// Watches a batch go by.
/// <para>
/// The shape people already know from Explorer — a bar, a speed, a time remaining — but with
/// the figures held to this app's rule. Every number on screen was measured: the bytes are
/// counted off the lines robocopy printed as each file landed, the speed is those bytes over
/// a stopwatch this window owns, and the estimate is that speed divided into what is left.
/// </para>
/// <para>
/// Where a figure is not knowable it says so rather than showing a placeholder. In the first
/// second there is no rate yet, so there is no estimate, and the line reads "working it out"
/// instead of a confident "0 seconds". The one thing this window never does is move the bar
/// because time passed.
/// </para>
/// </summary>
public partial class TransferWindow : Window
{
    private readonly TransferPlan _plan;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly System.Windows.Threading.DispatcherTimer _copyHintTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3),
    };

    private bool _finished;

    /// <summary>What the run did, available once the window has closed.</summary>
    public TransferReport? Report { get; private set; }

    private TransferWindow(TransferPlan plan)
    {
        _plan = plan;
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        SpeedLabel.Text = L.T("transfer.speed");
        FileRateLabel.Text = L.T("transfer.fileRate");
        RemainingLabel.Text = L.T("transfer.remaining");
        BytesLabel.Text = L.T("transfer.transferred");
        ItemsLabel.Text = L.T("transfer.items");
        ElapsedLabel.Text = L.T("transfer.elapsed");
        CancelButton.Content = L.T("transfer.cancel");
        CloseButton.Content = L.T("transfer.close");

        Title = TitleFor(plan.Kind);
        TitleText.Text = Title;
        DestinationText.Text = plan.Kind == TransferKind.Delete
            ? L.T("transfer.deleteSubtitle", Format.Count(plan.Count), Format.Bytes(plan.Bytes))
            : L.T("transfer.into", plan.Destination);

        _copyHintTimer.Tick += (_, _) =>
        {
            _copyHintTimer.Stop();
            CopyHint.Text = L.T("transfer.clickToCopy");
        };

        Reset();

        Loaded += async (_, _) => await RunAsync();
    }

    private static string TitleFor(TransferKind kind) => L.T(kind switch
    {
        TransferKind.Copy => "transfer.titleCopy",
        TransferKind.Move => "transfer.titleMove",
        _ => "transfer.titleDelete",
    });

    /// <summary>
    /// Runs <paramref name="plan"/>, showing progress until it finishes or is cancelled.
    /// </summary>
    /// <returns>What happened, or null when the plan had nothing to do.</returns>
    public static TransferReport? Run(Window owner, TransferPlan plan)
    {
        if (plan.IsEmpty) return null;

        var window = new TransferWindow(plan) { Owner = owner };
        window.ShowDialog();
        return window.Report;
    }

    private void Reset()
    {
        CurrentText.Text = L.T("transfer.preparing");
        PercentText.Text = string.Empty;
        SpeedValue.Text = "—";
        FileRateValue.Text = "—";
        RemainingValue.Text = L.T("transfer.notYetKnown");
        BytesValue.Text = $"0 / {Format.Bytes(_plan.Bytes)}";
        ItemsValue.Text = $"0 / {Format.Count(_plan.Count)}";
        ElapsedValue.Text = Format.Duration(TimeSpan.Zero);
        Bar.IsIndeterminate = _plan.Bytes <= 0;
    }

    private async Task RunAsync()
    {
        var service = new FileTransferService();

        // Progress<T> posts to the thread that created it, which is this one — so the
        // handler runs on the UI thread and nothing here has to marshal.
        var progress = new Progress<TransferProgress>(Show);

        try
        {
            Report = await service.ExecuteAsync(_plan, progress, _cancellation.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or System.ComponentModel.Win32Exception)
        {
            Report = new TransferReport(_plan.Kind, _plan.Destination, [],
                                        TransferPhase.Failed, 0, false, TimeSpan.Zero, ex.Message);
        }

        Finish(Report);
    }

    private void Show(TransferProgress progress)
    {
        if (_finished) return;

        double? fraction = progress.Fraction;

        if (fraction is null)
        {
            Bar.IsIndeterminate = true;
        }
        else
        {
            Bar.IsIndeterminate = false;
            Bar.Value = fraction.Value * 100;
        }

        CurrentText.Text = progress.CurrentItem.Length > 0
            ? progress.CurrentItem
            : L.T("transfer.preparing");

        // Two percentages would be one too many. The overall figure is the one that answers
        // "how much longer"; the per-file one only appears while a single large file is the
        // whole job, which is exactly when the overall bar would otherwise never move.
        // ⚠️ Format.Percent takes 0..100, and Fraction is 0..1. Handed the fraction straight
        // it read "0.4%" beside a bar sitting at 37% and "214 MiB / 573 MiB" — three numbers
        // for one quantity, two of them right.
        PercentText.Text = fraction is not null
            ? Format.Percent(fraction.Value * 100)
            : progress.CurrentFilePercent is int percent
                ? L.T("transfer.thisFile", percent)
                : string.Empty;

        SpeedValue.Text = progress.BytesPerSecond > 0
            ? L.T("transfer.perSecond", Format.Bytes((long)progress.BytesPerSecond))
            : "—";

        FileRateValue.Text = progress.FilesPerSecond > 0
            ? L.T("transfer.filesPerSecond", progress.FilesPerSecond.ToString("N1", L.Culture))
            : "—";

        RemainingValue.Text = progress.Remaining is TimeSpan remaining
            ? Format.Duration(remaining)
            : L.T("transfer.notYetKnown");

        BytesValue.Text = $"{Format.Bytes(progress.BytesDone)} / {Format.Bytes(progress.BytesTotal)}";
        ItemsValue.Text = $"{Format.Count(progress.FilesDone)} / {Format.Count(progress.FilesTotal)}";
        ElapsedValue.Text = Format.Duration(progress.Elapsed);
    }

    private void Finish(TransferReport? report)
    {
        _finished = true;

        // Nothing is moving any more, so there is no rate to show. Leaving the last reading
        // on screen would be a live figure that stopped being live — and the average over the
        // whole run is right there in the line below, which is the honest version of it.
        SpeedValue.Text = "—";

        Bar.IsIndeterminate = false;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.Focus();

        if (report is null)
        {
            Close();
            return;
        }

        Bar.Value = report.Phase == TransferPhase.Finished ? 100 : Bar.Value;

        OutcomeCard.Visibility = Visibility.Visible;
        OutcomeText.Text = Describe(report);

        List<TransferItemResult> failures = [.. report.Failures];

        if (failures.Count > 0)
        {
            FailureText.Visibility = Visibility.Visible;
            FailureText.Text = string.Join(Environment.NewLine,
                failures.Take(20).Select(f => $"{f.Item.Source} — {Reason(f)}"));
        }

        ShowFailedFiles(report);

        CurrentText.Text = string.Empty;
        PercentText.Text = string.Empty;
    }

    /// <summary>
    /// The files that did not make it, by name.
    /// <para>
    /// The card above reports items, and an item is often a folder: a copy of one folder
    /// whose contents partly failed said so in a single line and left no way to find out
    /// which files were lost. These are the paths the tool named as it worked.
    /// </para>
    /// </summary>
    private void ShowFailedFiles(TransferReport report)
    {
        IReadOnlyList<string> named = report.FailedFilePaths;
        if (named.Count == 0) return;

        FailedFilesSection.Visibility = Visibility.Visible;
        FailedFilesList.ItemsSource = named;
        CopyHint.Text = L.T("transfer.clickToCopy");
        SetToggleText();

        // Two readings of the same reality, compared here rather than by the person: the
        // paths named one by one, and the count from the tool's own closing table. One error
        // can sink a whole directory without naming what was inside it, so the list can be
        // the shorter of the two — and when it is, it says so instead of passing for the lot.
        if (report.FailedFileCount > named.Count)
        {
            FailedFilesNote.Visibility = Visibility.Visible;
            FailedFilesNote.Text = L.T("transfer.failedListPartial",
                                       Format.Count(named.Count), Format.Count(report.FailedFileCount));
        }
    }

    private void SetToggleText()
    {
        int count = FailedFilesList.Items.Count;
        bool open = FailedFilesPanel.Visibility == Visibility.Visible;

        FailedFilesToggle.Content = open
            ? L.T("transfer.hideFailedFiles")
            : count == 1
                ? L.T("transfer.showFailedFilesOne")
                : L.T("transfer.showFailedFiles", Format.Count(count));
    }

    /// <summary>
    /// Puts a failed path on the clipboard, and says so.
    /// <para>
    /// The clipboard is a shared thing another process can be holding, and it throws when it
    /// is. That is worth a sentence rather than a silent nothing: the person clicked, and has
    /// to know whether to paste or to select by hand.
    /// </para>
    /// </summary>
    private void OnCopyPath(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not TextBlock block || block.Text.Length == 0) return;

        try
        {
            Clipboard.SetDataObject(block.Text, copy: true);
            CopyHint.Text = L.T("transfer.pathCopied");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                        or InvalidOperationException)
        {
            CopyHint.Text = L.T("transfer.pathNotCopied");
        }

        // Back to the instruction after a moment, so the next click has something to change.
        _copyHintTimer.Stop();
        _copyHintTimer.Start();
    }

    private void OnToggleFailedFiles(object sender, RoutedEventArgs e)
    {
        FailedFilesPanel.Visibility = FailedFilesPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        SetToggleText();
    }

    private static string Describe(TransferReport report)
    {
        // "1 items did not make it" is the kind of line this project has already gone back
        // and fixed once, in the delete summary. Same treatment here.
        string headline = report.Phase switch
        {
            TransferPhase.Cancelled => L.T("transfer.cancelled", Format.Count(report.DoneCount)),
            TransferPhase.Failed => report.FailedCount == 1
                ? L.T("transfer.someFailedOne")
                : L.T("transfer.someFailed", Format.Count(report.FailedCount)),
            _ => report.DoneCount == 1
                ? L.T("transfer.doneOne")
                : L.T("transfer.done", Format.Count(report.DoneCount)),
        };

        // "Transferred" and "freed" are different claims, and only a permanent delete or a
        // move that left the volume can make the second one. Copying frees nothing, and the
        // sentence says so rather than leaving the byte count to be read as a saving.
        string bytes = report.BytesWereFreed
            ? L.T("transfer.freed", Format.Bytes(report.BytesTransferred))
            : L.T("transfer.movedBytes", Format.Bytes(report.BytesTransferred));

        string timing = L.T("transfer.tookAndAveraged",
            Format.Duration(report.Elapsed),
            report.Elapsed.TotalSeconds > 0
                ? Format.Bytes((long)(report.BytesTransferred / report.Elapsed.TotalSeconds))
                : "—");

        var parts = new List<string> { headline, bytes, timing };

        // The one place the app admits its own two readings disagreed, rather than quoting
        // whichever is larger.
        if (report.TotalIsUncertain) parts.Add(L.T("transfer.totalUncertain"));
        if (report.Message is not null) parts.Add(report.Message);

        return string.Join("  ·  ", parts);
    }

    private static string Reason(TransferItemResult result) => result.Message ?? L.T(result.Outcome switch
    {
        TransferOutcome.Blocked => "delete.outcomeBlocked",
        TransferOutcome.NotFound => "delete.outcomeNotFound",
        TransferOutcome.IntoItself => "move.outcomeIntoItself",
        TransferOutcome.Cancelled => "transfer.itemCancelled",
        _ => "delete.outcomeFailed",
    });

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Whatever already arrived stays where it is. Cancelling a copy halfway does not
        // reach back and undo the files that landed, and the report says how many did.
        CancelButton.IsEnabled = false;
        CancelButton.Content = L.T("transfer.cancelling");
        _cancellation.Cancel();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing with the X while it is still running means the same thing the button does.
        if (!_finished)
        {
            e.Cancel = true;
            _cancellation.Cancel();
            return;
        }

        _cancellation.Dispose();
        base.OnClosing(e);
    }
}
