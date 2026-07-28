using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Telemetry;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;

/// <summary>
/// The debounced incremental flush (PLAN §4, §7).
///
/// Not a <see cref="Common.Scheduling.PeriodicWorker"/>, deliberately. A timer that ticks
/// every 30 seconds forever would query for dirty rows 2,880 times a day on a catalogue where
/// expiries arrive in a handful of bursts. This waits on a signal instead, so it costs
/// nothing at all when nothing has changed, and reacts within one debounce window when
/// something has.
///
/// The loop is: wait for a signal → sleep the debounce window so the rest of a burst piles up
/// behind it → drain → flush once. A hundred expiries arriving together therefore produce ONE
/// re-projection of <c>sitemap-jobs</c> rather than a hundred, of which ninety-nine would
/// have been obsolete before they finished uploading.
///
/// Everything here goes through <see cref="TimeProvider"/> rather than <c>Task.Delay</c>
/// directly, so the coalescing behaviour can be asserted with a <c>FakeTimeProvider</c> in
/// milliseconds instead of by sleeping through real debounce windows in a flaky test.
/// </summary>
public sealed class FlushDirtyWorker(
    IServiceScopeFactory scopes,
    DirtySignal signal,
    IOptions<SeoOptions> options,
    TimeProvider clock,
    ILogger<FlushDirtyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan window = options.Value.DebounceWindow;

        logger.LogInformation(
            "Dirty flush waiting on the coalescing signal with a {Window} debounce window.", window);

        // Boot recovery, before anything else.
        //
        // The in-memory signal died with the previous process. If that process was killed
        // between a consumer's COMMIT and its flush, the dirty rows are still there and
        // nothing would raise a signal for them — the sitemap would keep advertising a
        // withdrawn job until the next full rebuild, up to a whole cron interval away, with
        // no log line anywhere saying why.
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stoppingToken);

                // The debounce. Everything that arrives during this window is absorbed by
                // the drain below and covered by the single flush that follows.
                await Task.Delay(window, clock, stoppingToken);

                // Drained BEFORE the flush, not after: see DirtySignal.Drain. A change
                // committed while the projection is streaming must leave its signal behind so
                // the next iteration picks it up.
                signal.Drain();

                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticsConfig.WorkerTickFailures.Add(1,
                    new KeyValuePair<string, object?>("worker", "flush-dirty"));

                // The loop survives, and the rows stay dirty because ClearDirtyAsync only runs
                // inside the committed transaction. So a failed flush is retried by the next
                // signal or by the next full rebuild — never silently dropped.
                logger.LogError(ex, "Dirty flush failed. The rows stay dirty and will be retried.");

                // A small backoff, so a persistent failure — an unreachable bucket, say —
                // does not spin against a signal that keeps being re-raised.
                try
                {
                    await Task.Delay(window, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Dirty flush stopped.");
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            Common.Persistence.ISeoStore store =
                scope.ServiceProvider.GetRequiredService<Common.Persistence.ISeoStore>();

            int dirty = await store.CountDirtyAsync(cancellationToken);

            if (dirty == 0)
            {
                return;
            }

            logger.LogWarning(
                "Found {Dirty} unflushed row(s) at startup — the previous process did not "
                + "complete a flush. Flushing now.", dirty);

            signal.Raise();
        }
        catch (Exception ex)
        {
            // Never fatal. A database that is not ready yet must not stop this worker from
            // starting, because the cron rebuild will correct everything within one interval
            // regardless.
            logger.LogWarning(ex, "Could not check for unflushed rows at startup.");
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        IDirtyFlusher flusher = scope.ServiceProvider.GetRequiredService<IDirtyFlusher>();

        await flusher.FlushAsync(cancellationToken);
    }
}
