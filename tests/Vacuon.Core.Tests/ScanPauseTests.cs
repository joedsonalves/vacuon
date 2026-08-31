using System.Diagnostics;
using Vacuon.Core.Scan;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>Holding a scan and letting it go again (PRD F1.4).</summary>
public class ScanPauseTests
{
    [Fact]
    public void NotPausedCostsNothingAndReturnsAtOnce()
    {
        // This is called once per 8 MiB block of every scan, so the ordinary path has to be
        // free. An event that is already set is exactly that.
        var pause = new ScanPause();

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++) pause.WaitIfPaused(CancellationToken.None);
        clock.Stop();

        Assert.False(pause.IsPaused);
        Assert.True(clock.ElapsedMilliseconds < 200, $"10.000 chamadas levaram {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task APausedScanWaits_AndAResumedOneCarriesOn()
    {
        var pause = new ScanPause();
        pause.Pause();

        var released = new TaskCompletionSource();
        var worker = Task.Run(() =>
        {
            pause.WaitIfPaused(CancellationToken.None);
            released.SetResult();
        });

        // Still held after a moment: this is the assertion that it actually blocks.
        Assert.NotSame(released.Task, await Task.WhenAny(released.Task, Task.Delay(150)));

        pause.Resume();
        await worker;

        Assert.True(released.Task.IsCompletedSuccessfully);
        Assert.False(pause.IsPaused);
    }

    [Fact]
    public async Task StoppingWhilePausedDoesNotWaitForSomebodyToPressResume()
    {
        // Cancelling has to work while it is held, or the only way out of a pause would be
        // to un-pause first — and somebody who wants to stop does not want to resume.
        var pause = new ScanPause();
        pause.Pause();

        using var cancel = new CancellationTokenSource();
        Task worker = Task.Run(() => pause.WaitIfPaused(cancel.Token));

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task TheClockStopsWithIt()
    {
        // ⚠️ The scan reports records per second and megabytes per second, both of them real
        // work divided by elapsed time. A scan held for five minutes and resumed would
        // report a rate an order of magnitude below what the disk did — a figure nobody
        // measured, arrived at by dividing work that happened by time that did not.
        var pause = new ScanPause();

        Assert.Equal(TimeSpan.Zero, pause.HeldFor);

        pause.Pause();
        await Task.Delay(120);
        pause.Resume();

        TimeSpan held = pause.HeldFor;
        Assert.True(held >= TimeSpan.FromMilliseconds(80), $"contou {held.TotalMilliseconds} ms");

        // And it does not keep counting once the scan is running again.
        await Task.Delay(120);
        Assert.True(pause.HeldFor - held < TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public void PausingTwiceIsPausedOnce()
    {
        var pause = new ScanPause();

        pause.Pause();
        pause.Pause();
        pause.Resume();

        Assert.False(pause.IsPaused);
        pause.WaitIfPaused(CancellationToken.None);   // não bloqueia
    }

    [Fact]
    public void TheHeldTimeAddsUpAcrossPauses()
    {
        var pause = new ScanPause();

        pause.Pause();
        Thread.Sleep(60);
        pause.Resume();
        TimeSpan first = pause.HeldFor;

        pause.Pause();
        Thread.Sleep(60);
        pause.Resume();

        Assert.True(pause.HeldFor > first);
    }
}
