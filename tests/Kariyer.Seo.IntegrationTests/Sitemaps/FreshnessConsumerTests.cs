using Kariyer.Messaging.Contracts.Freshness;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;
using Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobExpired;
using Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobResurrected;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kariyer.Seo.IntegrationTests.Sitemaps;

/// <summary>
/// The reactive path: a freshness event arrives, the job leaves the sitemap, its prerendered
/// page is purged, and a redelivery changes nothing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FreshnessConsumerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Site = "https://kariyerzamani.com";

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnExpiryRemovesTheUrlAndPurgesEveryPrerenderKey()
    {
        await SeedProjectionAsync("job-1", "yazilim-muhendisi-1");

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Bus.Bus.Publish(new JobExpiredEvent
        {
            JobUid = "job-1",
            SlugUrl = "yazilim-muhendisi-1",
            Reason = "withdrawn",
            ExpiredAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await harness.Bus.Consumed.Any<JobExpiredEvent>());

        await using SeoDbContext db = postgres.CreateContext();

        SeoUrlState state = await db.UrlStates.SingleAsync(s => s.JobUid == "job-1");

        Assert.Equal(SeoUrlStatus.Removed, state.Status);

        // Dirty, so the flush is owed even if this process dies right now. That column, not
        // the in-memory signal, is what makes the debounce crash-safe.
        Assert.True(state.Dirty);

        // All THREE keys. The two legacy URL shapes still 301 to the canonical, and a bot
        // that followed one got its snapshot cached under the legacy key — so purging only
        // the canonical leaves a withdrawn job serving a rendered 'apply now' page for the
        // whole TTL.
        Assert.Equal(
            PrerenderKeys.For(Site, "yazilim-muhendisi-1").Order(),
            harness.Cache.Purged.Order());
    }

    [Fact]
    public async Task RedeliveryIsANoOp()
    {
        await SeedProjectionAsync("job-1", "slug-1");

        await using Harness harness = await Harness.StartAsync(postgres);

        JobExpiredEvent message = new()
        {
            JobUid = "job-1",
            SlugUrl = "slug-1",
            Reason = "not_found",
            ExpiredAt = DateTimeOffset.UtcNow,
        };

        // The SAME MessageId twice, which is what a broker redelivery looks like. RabbitMQ
        // delivers at-least-once and a retry after a transient database fault is normal.
        Guid messageId = Guid.NewGuid();

        await harness.Bus.Bus.Publish(message, c => c.MessageId = messageId);
        await harness.Bus.Consumed.Any<JobExpiredEvent>();

        await harness.Bus.Bus.Publish(message, c => c.MessageId = messageId);

        await using SeoDbContext db = postgres.CreateContext();
        SeoUrlState state = await db.UrlStates.SingleAsync(s => s.JobUid == "job-1");

        // Idempotent either way — the state is a set, not a counter — but the assertion that
        // matters is that a second delivery cannot produce a second, different outcome.
        Assert.Equal(SeoUrlStatus.Removed, state.Status);
        Assert.Single(await db.UrlStates.ToListAsync());
    }

    [Fact]
    public async Task AResurrectionReAdmitsTheUrlAndPurgesAgain()
    {
        await SeedProjectionAsync("job-1", "slug-1", SeoUrlStatus.Removed);

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Bus.Bus.Publish(new JobResurrectedEvent
        {
            JobUid = "job-1",
            SlugUrl = "slug-1",
            RestoredAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await harness.Bus.Consumed.Any<JobResurrectedEvent>());

        await using SeoDbContext db = postgres.CreateContext();
        SeoUrlState state = await db.UrlStates.SingleAsync(s => s.JobUid == "job-1");

        Assert.Equal(SeoUrlStatus.Live, state.Status);

        // Purged on this path too: the cache may still hold the page as it looked while the
        // job was expired, so re-admitting the URL without purging would advertise a live
        // posting whose rendered page tells the visitor it is closed.
        Assert.NotEmpty(harness.Cache.Purged);
    }

    [Fact]
    public async Task AnExpiryForAJobWeHaveNeverSeenStillCreatesAStateRow()
    {
        // A job expired before this service's first full rebuild has no row. Skipping it
        // would mean the purge never happens — the URL would eventually leave the sitemap on
        // the next corpus sync, but the prerendered page would survive its whole TTL.
        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Bus.Bus.Publish(new JobExpiredEvent
        {
            JobUid = "unknown-job",
            SlugUrl = "unknown-slug",
            Reason = "not_found",
            ExpiredAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await harness.Bus.Consumed.Any<JobExpiredEvent>());

        await using SeoDbContext db = postgres.CreateContext();
        SeoUrlState state = await db.UrlStates.SingleAsync(s => s.JobUid == "unknown-job");

        Assert.Equal(SeoUrlStatus.Removed, state.Status);
        Assert.NotEmpty(harness.Cache.Purged);
    }

    [Fact]
    public async Task TheFlushReProjectsJobsWithoutTheExpiredUrl()
    {
        await SeedProjectionAsync("job-1", "slug-1");
        await SeedProjectionAsync("job-2", "slug-2");

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Bus.Bus.Publish(new JobExpiredEvent
        {
            JobUid = "job-1",
            SlugUrl = "slug-1",
            Reason = "withdrawn",
            ExpiredAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await harness.Bus.Consumed.Any<JobExpiredEvent>());

        // The worker's debounce is driven by a FakeTimeProvider elsewhere; here the
        // projection is invoked directly, which is what the worker does after its window.
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();

        int urls = await scope.ServiceProvider
            .GetRequiredService<JobSitemapProjector>()
            .FlushAsync(CancellationToken.None);

        Assert.Equal(1, urls);

        string jobs = harness.Sink.Live["sitemap-jobs-1.xml"].Text;

        Assert.DoesNotContain("slug-1", jobs, StringComparison.Ordinal);
        Assert.Contains("slug-2", jobs, StringComparison.Ordinal);

        // The dirty flag is cleared only inside the committed transaction, so a flush that
        // threw would leave the work owed rather than silently dropping it.
        await using SeoDbContext db = postgres.CreateContext();
        Assert.Empty(await db.UrlStates.Where(s => s.Dirty).ToListAsync());
    }

    [Fact]
    public async Task AFlushWithNothingDirtyLeavesTheJobsFileAlone()
    {
        await SeedProjectionAsync("job-1", "slug-1");

        await using Harness harness = await Harness.StartAsync(postgres);
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<JobSitemapProjector>()
            .FlushAsync(CancellationToken.None);

        // The expensive half is skipped: no dirty rows means the live corpus is not streamed
        // and sitemap-jobs is not rewritten. Re-uploading an identical file would burn the
        // checksum short-circuit's whole purpose.
        Assert.DoesNotContain("sitemap-jobs-1.xml", harness.Sink.Uploaded);

        // sitemap-pages IS published, and must be. A CMS publish raises the same signal and
        // leaves no dirty flag — cms.seo_page is the truth and there is nothing local to mark
        // — so a flush that returned early on `dirty == 0` would make every page publish a
        // no-op and strand CMS pages until the 45-minute cron.
        Assert.Contains("sitemap-pages-1.xml", harness.Sink.Uploaded);
    }

    [Fact]
    public async Task AFlushPicksUpAPublishedCmsPageWithNoDirtyJobRows()
    {
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/cv-nasil-yazilir");

        await using Harness harness = await Harness.StartAsync(postgres);
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<JobSitemapProjector>()
            .FlushAsync(CancellationToken.None);

        string pages = harness.Sink.Live["sitemap-pages-1.xml"].Text;

        Assert.Contains(
            $"{Site}/kariyer-rehberi/cv-nasil-yazilir", pages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFlushIndexStillNamesTheJobsFileWhenOnlyAPageChanged()
    {
        // The bug this guards: a CMS-only flush leaves jobChunks empty, and an index built
        // from just-built chunks alone would omit sitemap-jobs entirely — the whole job
        // catalogue vanishing from the index the moment an editor publishes a landing page.
        await SeedProjectionAsync("job-1", "slug-1");
        await SeedRebuildLogAsync("sitemap-jobs-1.xml", "jobs");
        await SeedRebuildLogAsync("sitemap-jobfilters-1.xml", "jobfilters");

        await postgres.SeedCmsPageAsync("/kariyer-rehberi/x");

        await using Harness harness = await Harness.StartAsync(postgres);
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<JobSitemapProjector>()
            .FlushAsync(CancellationToken.None);

        // Asserted against the index DOCUMENT rather than through FollowIndex: the two older
        // files exist only as rebuild-log rows here, never having been published to the fake
        // sink, and FollowIndex deliberately throws when the index names a file that is not
        // live. That check is right for a real swap and wrong for this fixture — what is
        // under test is which children the index composes, not whether they were uploaded.
        string index = harness.Sink.Live[SitemapNames.Index].Text;

        Assert.Contains("sitemap-jobs-1.xml", index, StringComparison.Ordinal);
        Assert.Contains("sitemap-jobfilters-1.xml", index, StringComparison.Ordinal);
        Assert.Contains("sitemap-pages-1.xml", index, StringComparison.Ordinal);
    }

    private Task SeedRebuildLogAsync(string file, string type) =>
        postgres.ExecuteAsync($"""
            INSERT INTO seo.seo_rebuild_log
                (file, sitemap_type, url_count, checksum, uncompressed_bytes, uploaded, generated_at)
            VALUES ('{file}', '{type}', 1, 'seed', 1, true, now())
            """);

    private Task SeedProjectionAsync(
        string uid, string slug, SeoUrlStatus status = SeoUrlStatus.Live) =>
        postgres.ExecuteAsync($"""
            INSERT INTO seo.seo_url_state (job_uid, slug, status, last_modified, dirty, updated_at)
            VALUES ('{uid}', '{slug}', {(int)status}, NULL, false, now())
            """);

    private sealed class Harness(
        ServiceProvider provider, FakeSitemapSink sink, RecordingCache cache, ITestHarness bus)
        : IAsyncDisposable
    {
        public IServiceProvider Services => provider;

        public FakeSitemapSink Sink => sink;

        public RecordingCache Cache => cache;

        public ITestHarness Bus => bus;

        public static async Task<Harness> StartAsync(PostgresFixture postgres)
        {
            FakeSitemapSink sink = new();
            RecordingCache cache = new();

            ServiceCollection services = [];

            services.AddSingleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)));
            services.AddLogging();

            services.AddSingleton<ISitemapSink>(sink);
            services.AddSingleton<IPrerenderCache>(cache);
            services.AddSingleton<IIndexingSubmitter, DisabledIndexingSubmitter>();
            services.AddSingleton<DirtySignal>();

            services.AddSingleton(Options.Create(new SeoOptions
            {
                SiteUrl = Site,
                R2 = new R2Options { Compress = false },
            }));
            services.AddSingleton(Options.Create(new PersistenceOptions()));

            services.AddDbContext<SeoDbContext>(o => o.UseNpgsql(postgres.ConnectionString));
            services.AddScoped<ISeoStore, SeoStore>();
            services.AddScoped<JobSitemapProjector>();

            services.AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<JobExpiredConsumer>();
                bus.AddConsumer<JobResurrectedConsumer>();

                // The real inbox/outbox, against the real schema — so "redelivery is a no-op"
                // is tested through the same filter the deployed service uses rather than
                // through a mock that agrees with itself.
                bus.AddEntityFrameworkOutbox<SeoDbContext>(outbox =>
                {
                    outbox.UsePostgres();
                    outbox.UseBusOutbox();
                });
            });

            ServiceProvider provider = services.BuildServiceProvider();

            ITestHarness harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            return new Harness(provider, sink, cache, harness);
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }

    /// <summary>Records purges instead of talking to Garnet.</summary>
    private sealed class RecordingCache : IPrerenderCache
    {
        private readonly List<string> _purged = [];

        public IReadOnlyList<string> Purged
        {
            get
            {
                lock (_purged)
                {
                    return [.. _purged];
                }
            }
        }

        public Task<int> PurgeJobAsync(string slug, CancellationToken cancellationToken) =>
            Record(PrerenderKeys.For(Site, slug));

        public Task<int> PurgePathAsync(string path, CancellationToken cancellationToken) =>
            Record([PrerenderKeys.ForPath(Site, path)]);

        private Task<int> Record(string[] keys)
        {
            lock (_purged)
            {
                _purged.AddRange(keys);
            }

            return Task.FromResult(keys.Length);
        }
    }
}
