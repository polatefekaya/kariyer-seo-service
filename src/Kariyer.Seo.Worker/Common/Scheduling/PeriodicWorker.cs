using Kariyer.Seo.Worker.Common.Telemetry;

namespace Kariyer.Seo.Worker.Common.Scheduling;

/// <summary>
/// Base for the service's recurring work.
///
/// The contract that matters: <b>one bad tick must never stop the loop.</b> A rebuild
/// touches Postgres, an object store and an HTTP endpoint in another repository, so
/// transient faults are certain. A hosted service that dies on the first unhandled
/// exception looks perfectly healthy — the pod stays up, health checks pass — while the
/// sitemap has not been rebuilt for hours.
///
/// That is worse here than almost anywhere else in the estate, because this service has no
/// user-facing failure mode at all: nothing 500s, no request is slow, and a frozen sitemap
/// looks exactly like a quiet catalogue. Silence is the worst property a background job can
/// have, and this one would be silent for weeks.
///
/// So every tick is wrapped, every failure is logged and counted, and the loop continues.
/// </summary>
public abstract class PeriodicWorker(ILogger logger) : BackgroundService
{
    /// <summary>How long to wait between ticks.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Name used in logs and metrics.</summary>
    protected abstract string Name { get; }

    /// <summary>
    /// One unit of recurring work. May throw; the loop survives it.
    ///
    /// Internal rather than private so integration tests can drive a tick directly. A test
    /// that instead started the host and slept would be both slow and flaky, and the thing
    /// worth asserting is what one tick DOES — not that a timer eventually fires.
    /// </summary>
    protected internal abstract Task TickAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Worker} starting on a {Interval} interval.", Name, Interval);

        // Let the host finish starting before the first tick, so a burst of database work
        // does not race readiness probes on a cold pod.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. Expected, not a fault.
                break;
            }
            catch (Exception ex)
            {
                DiagnosticsConfig.WorkerTickFailures.Add(1,
                    new KeyValuePair<string, object?>("worker", Name));

                logger.LogError(ex, "{Worker} tick failed. The loop continues.", Name);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("{Worker} stopped.", Name);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
