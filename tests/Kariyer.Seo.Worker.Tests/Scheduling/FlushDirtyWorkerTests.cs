using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Kariyer.Seo.Worker.Tests.Scheduling;

/// <summary>
/// The debounce, driven by a <see cref="FakeTimeProvider"/>.
///
/// What is being asserted is a COST property, not a correctness one — which is exactly why it
/// needs a test. A worker that flushed once per expiry instead of once per burst still
/// produces a correct sitemap; it just re-streams a several-hundred-thousand-URL file a
/// hundred times, of which ninety-nine are obsolete before they finish uploading. Nothing in
/// the service reports that. It surfaces as an R2 bill, months later.
/// </summary>
public sealed class FlushDirtyWorkerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ABurstOfSignalsProducesOneFlush()
    {
        Fixture fixture = new();

        await fixture.StartAsync();

        for (int i = 0; i < 100; i++)
        {
            fixture.Signal.Raise();
        }

        Assert.Equal(1, await fixture.AdvanceUntilFlushesAsync(1));

        await fixture.StopAsync();
    }

    [Fact]
    public async Task NothingFlushesBeforeTheWindowElapses()
    {
        Fixture fixture = new();
        await fixture.StartAsync();

        fixture.Signal.Raise();

        // One tick short of the window. Flushing here would mean the coalescing period is
        // not actually being observed and a burst would fan out into N projections.
        await fixture.AdvanceAsync(Window - TimeSpan.FromSeconds(1));

        Assert.Equal(0, fixture.Flusher.Flushes);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task ASignalRaisedDuringAFlushGetsItsOwnPass()
    {
        Fixture fixture = new();
        await fixture.StartAsync();

        fixture.Signal.Raise();
        Assert.Equal(1, await fixture.AdvanceUntilFlushesAsync(1));

        // A change committed WHILE the previous projection was streaming may have arrived
        // after its row was read, so it is not in the file that went live. Its signal must
        // survive the drain — otherwise that job sits in the wrong state until the next full
        // rebuild, invisibly, for up to 45 minutes.
        fixture.Signal.Raise();

        Assert.Equal(2, await fixture.AdvanceUntilFlushesAsync(2));

        await fixture.StopAsync();
    }

    [Fact]
    public async Task AFailedFlushDoesNotStopTheLoop()
    {
        Fixture fixture = new();
        fixture.Flusher.ThrowOnce = true;

        await fixture.StartAsync();

        fixture.Signal.Raise();
        Assert.Equal(1, await fixture.AdvanceUntilFlushesAsync(1));

        fixture.Signal.Raise();

        // The rows stay dirty either way — ClearDirtyAsync only runs inside the committed
        // transaction — so what must not happen is the loop dying and never retrying. The
        // advance also has to carry the loop through its post-failure backoff, which is why
        // this steps until the count moves rather than jumping a fixed amount.
        Assert.Equal(2, await fixture.AdvanceUntilFlushesAsync(2));

        await fixture.StopAsync();
    }

    [Fact]
    public async Task UnflushedRowsAtStartupRaiseASignal()
    {
        // The in-memory signal died with the previous process. If that process was killed
        // between a consumer's COMMIT and its flush, nothing would ever raise a signal for
        // those rows and the sitemap would keep advertising a withdrawn job until the next
        // full rebuild — with no log line anywhere saying why.
        Fixture fixture = new();
        fixture.Store.CountDirtyAsync(Arg.Any<CancellationToken>()).Returns(7);

        await fixture.StartAsync();

        Assert.Equal(1, await fixture.AdvanceUntilFlushesAsync(1));

        await fixture.StopAsync();
    }

    private sealed class Fixture
    {
        public FakeTimeProvider Clock { get; } =
            new(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));

        public DirtySignal Signal { get; } = new();

        public CountingFlusher Flusher { get; } = new();

        public ISeoStore Store { get; } = Substitute.For<ISeoStore>();

        private FlushDirtyWorker _worker = null!;

        public async Task StartAsync()
        {
            ServiceCollection services = [];
            services.AddScoped<IDirtyFlusher>(_ => Flusher);
            services.AddScoped(_ => Store);

            ServiceProvider provider = services.BuildServiceProvider();

            _worker = new FlushDirtyWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Signal,
                Options.Create(new SeoOptions { DebounceWindow = Window }),
                Clock,
                NullLogger<FlushDirtyWorker>.Instance);

            await _worker.StartAsync(CancellationToken.None);
        }

        public Task StopAsync() => _worker.StopAsync(CancellationToken.None);

        /// <summary>
        /// Advances the fake clock in small steps, with a real yield between each.
        ///
        /// One big Advance would be a race, not a test. FakeTimeProvider fires timers
        /// synchronously on Advance, so a timer the worker has not created YET is simply
        /// skipped — and since the worker only creates one after it has woken on the signal,
        /// a single jump would land before the timer exists and the assertion would pass or
        /// fail on scheduler luck rather than on the debounce.
        ///
        /// Stepping gives the worker a real chance to park on its delay first, and every
        /// subsequent step then lands on a timer that genuinely exists.
        /// </summary>
        public async Task AdvanceAsync(TimeSpan total)
        {
            TimeSpan step = TimeSpan.FromSeconds(1);
            TimeSpan advanced = TimeSpan.Zero;

            while (advanced < total)
            {
                await Task.Delay(10);

                TimeSpan tick = total - advanced < step ? total - advanced : step;
                Clock.Advance(tick);
                advanced += tick;
            }

            await Task.Delay(10);
        }

        /// <summary>
        /// Advances the fake clock a window at a time until the flush count reaches
        /// <paramref name="expected"/>, or gives up.
        ///
        /// Self-correcting, and it has to be. A fixed total advance is a race against timer
        /// CREATION: FakeTimeProvider fires only timers that already exist, so any advance
        /// landing before the loop has parked on its delay is silently swallowed, and the
        /// remaining advance no longer adds up to a full window. That made the failure-path
        /// test flaky roughly one run in five — it waits on a backoff timer created inside a
        /// catch block, which is the latest-created timer in the whole worker.
        ///
        /// Advancing until the observable outcome happens removes the dependency on when the
        /// timer appeared, without weakening what is asserted: the count still has to move,
        /// and a loop that died never moves it.
        /// </summary>
        public async Task<int> AdvanceUntilFlushesAsync(int expected)
        {
            for (int i = 0; i < 200 && Flusher.Flushes < expected; i++)
            {
                await Task.Delay(5);
                Clock.Advance(Window);
            }

            return Flusher.Flushes;
        }
    }

    private sealed class CountingFlusher : IDirtyFlusher
    {
        private int _flushes;

        public int Flushes => Volatile.Read(ref _flushes);

        public bool ThrowOnce { get; set; }

        public Task<int> FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushes);

            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("R2 is unreachable.");
            }

            return Task.FromResult(0);
        }
    }
}
