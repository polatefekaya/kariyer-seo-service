using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;

namespace Kariyer.Seo.Worker.Tests.Scheduling;

/// <summary>
/// The coalescing signal.
///
/// Its whole job is to turn a burst of a hundred expiries into ONE re-projection of
/// sitemap-jobs. Get it wrong and the service still produces correct output — it just
/// re-streams a several-hundred-thousand-URL file a hundred times, of which ninety-nine are
/// obsolete before they finish uploading. Nothing reports that; it shows up as an R2 bill.
/// </summary>
public sealed class DirtySignalTests
{
    [Fact]
    public async Task ABurstCoalescesIntoOneWait()
    {
        DirtySignal signal = new();

        for (int i = 0; i < 100; i++)
        {
            signal.Raise();
        }

        // The channel holds one slot with DropWrite, so ninety-nine of those were discarded
        // because a flush was already owed.
        await signal.WaitAsync(CancellationToken.None);

        signal.Drain();

        Assert.False(await HasPendingSignalAsync(signal));
    }

    [Fact]
    public async Task ASignalRaisedAfterTheDrainSurvives()
    {
        // The case that makes the ordering in FlushDirtyWorker load-bearing: a change
        // committed WHILE the projection is streaming may have arrived after its row was
        // read, so it is not in the file that went live. Its signal has to survive so the
        // next iteration flushes again — otherwise that one job sits in the wrong state until
        // the next full rebuild, invisibly, for up to 45 minutes.
        DirtySignal signal = new();

        signal.Raise();
        await signal.WaitAsync(CancellationToken.None);
        signal.Drain();

        signal.Raise();

        Assert.True(await HasPendingSignalAsync(signal));
    }

    [Fact]
    public void RaisingNeverBlocksEvenWhenNobodyIsListening()
    {
        // Raise() is called from inside a consumer's transaction path. If it could block on a
        // full channel, a burst of expiries would stall the consumers that produced it.
        DirtySignal signal = new();

        for (int i = 0; i < 10_000; i++)
        {
            signal.Raise();
        }
    }

    [Fact]
    public void DrainingAnEmptySignalIsANoOp() => new DirtySignal().Drain();

    /// <summary>
    /// Whether a signal is already sitting in the channel.
    ///
    /// A short real timeout rather than a pre-cancelled token: <c>ReadAsync</c> honours
    /// cancellation before it checks for buffered data, so a cancelled token reports "no
    /// signal" even when one is there — which would make the assertion that matters here
    /// pass for the wrong reason.
    /// </summary>
    private static async Task<bool> HasPendingSignalAsync(DirtySignal signal)
    {
        try
        {
            await signal.WaitAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250));

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
