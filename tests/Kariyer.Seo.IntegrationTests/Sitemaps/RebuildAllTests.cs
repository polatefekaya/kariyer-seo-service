using Kariyer.Messaging.Contracts.Seo;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kariyer.Seo.IntegrationTests.Sitemaps;

/// <summary>
/// The full rebuild, end to end over a real Postgres and a MassTransit harness.
///
/// This is where the pieces that pass unit tests individually get to disagree: the corpus
/// predicate in SQL versus the one the facet aggregate uses, the projection versus what
/// actually reaches the sink, the event versus the state behind it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RebuildAllTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Site = "https://kariyerzamani.com";

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProducesTheExactSitemapSetOverACorpusStandIn()
    {
        await postgres.SeedJobAsync("job-1", "yazilim-muhendisi-1",
            province: "İstanbul", department: "Bilişim",
            modifiedOn: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        await postgres.SeedJobAsync("job-2", "yazilim-muhendisi-2",
            province: "İstanbul", department: "Bilişim - İnternet",
            modifiedOn: new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero));

        // Not live: these must NOT appear. Both are ways a job leaves the corpus WITHOUT any
        // freshness event ever being published — which is the majority of departures.
        await postgres.SeedJobAsync("job-expired", "expired-1", status: "expired");
        await postgres.SeedJobAsync("job-inactive", "inactive-1", isActive: false);

        await using Harness harness = await Harness.StartAsync(postgres);

        RebuildOutcome outcome = await harness.Builder.RebuildAsync("test", CancellationToken.None);

        // Six files: one chunk each of jobs, jobfilters, pages and static, plus the index and
        // robots.txt.
        Assert.Equal(6, outcome.Files);

        IReadOnlyDictionary<string, StoredFile> live = harness.Sink.Live;

        Assert.Contains("sitemap.xml", live);
        Assert.Contains("sitemap-jobs-1.xml", live);
        Assert.Contains("sitemap-jobfilters-1.xml", live);
        Assert.Contains("sitemap-static-1.xml", live);
        Assert.Contains("robots.txt", live);

        // Published even with no CMS pages seeded. An empty file is the honest statement, and
        // it means the index never names a child that does not exist.
        Assert.Contains("sitemap-pages-1.xml", live);

        string jobs = live["sitemap-jobs-1.xml"].Text;

        Assert.Contains($"{Site}/is-ilanlari/ilan/yazilim-muhendisi-1", jobs, StringComparison.Ordinal);
        Assert.Contains($"{Site}/is-ilanlari/ilan/yazilim-muhendisi-2", jobs, StringComparison.Ordinal);
        Assert.DoesNotContain("expired-1", jobs, StringComparison.Ordinal);
        Assert.DoesNotContain("inactive-1", jobs, StringComparison.Ordinal);

        // lastmod comes from company_job.modified_on, not from the rebuild time.
        Assert.Contains("<lastmod>2026-07-01T09:00:00Z</lastmod>", jobs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishesSitemapRebuiltThroughTheOutbox()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        // Asserted by RECEIPT on the harness, not by inspecting the outbox table: what
        // matters is that the message actually left, which is the half a split commit would
        // lose.
        Assert.True(await harness.Bus.Published.Any<SitemapRebuiltEvent>());

        IReadOnlyList<SitemapRebuiltEvent> events =
        [
            .. harness.Bus.Published.Select<SitemapRebuiltEvent>().Select(p => p.Context.Message),
        ];

        SitemapRebuiltEvent jobs = Assert.Single(
            events.Where(e => e.SitemapType == SitemapNames.Types.Jobs));

        Assert.Equal(1, jobs.UrlCount);
        Assert.Equal(64, jobs.Checksum.Length);

        // The rebuild log and the event must agree — they were written in the same commit.
        await using SeoDbContext db = postgres.CreateContext();
        SeoStore store = postgres.CreateStore(db);

        IReadOnlyDictionary<string, string> checksums =
            await store.GetLastChecksumsAsync(CancellationToken.None);

        Assert.Equal(jobs.Checksum, checksums["sitemap-jobs-1.xml"]);
    }

    [Fact]
    public async Task ASecondRebuildOverAnUnchangedCorpusUploadsNothing()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Builder.RebuildAsync("first", CancellationToken.None);

        harness.Sink.Uploaded.Clear();

        RebuildOutcome second = await harness.Builder.RebuildAsync("second", CancellationToken.None);

        // The healthy steady state (PLAN §7). Most cron ticks change nothing, and re-uploading
        // an identical set every 45 minutes would also make SitemapRebuiltEvent a heartbeat
        // nobody could distinguish from real news.
        Assert.Equal(0, second.Changed);
        Assert.Empty(harness.Sink.Uploaded);
    }

    [Fact]
    public async Task AChangedCorpusUploadsAgain()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("first", CancellationToken.None);

        harness.Sink.Uploaded.Clear();

        await postgres.SeedJobAsync("job-2", "slug-2", province: "İstanbul");

        RebuildOutcome second = await harness.Builder.RebuildAsync("second", CancellationToken.None);

        Assert.True(second.Changed > 0);
        Assert.Contains("sitemap-jobs-1.xml", harness.Sink.Uploaded);
        Assert.Contains("slug-2", harness.Sink.Live["sitemap-jobs-1.xml"].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveIndexNeverNamesAFileThatIsNotPublished()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        // FollowIndex reads the way a crawler does — fetch the index, then fetch every child
        // it names — and throws if one is missing. That is the whole atomicity claim of
        // PLAN §6.3 reduced to an assertion.
        IReadOnlyList<string> children = harness.Sink.FollowIndex(SitemapNames.Index);

        Assert.Contains("sitemap-jobs-1.xml", children);
        Assert.Contains("sitemap-jobfilters-1.xml", children);
        Assert.Contains("sitemap-pages-1.xml", children);
        Assert.Contains("sitemap-static-1.xml", children);
    }

    [Fact]
    public async Task NoPublishedFileRepeatsAUrlOrARule()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");
        await postgres.SeedCmsPageAsync("/kariyer-rehberi");

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        // The published output, not the options object — which is the only place the original
        // defect was ever visible. Both lists had non-empty C# initialisers, the configuration
        // binder appends rather than replaces, and the live bucket ended up serving a
        // sitemap-static-1.xml with 14 <url> entries for 7 pages and a robots.txt with every
        // Disallow line twice. Duplicate <loc> is invalid per the sitemaps protocol, and no
        // test that looked at the options class alone could see it.
        foreach ((string name, StoredFile file) in harness.Sink.Live)
        {
            if (name == SitemapNames.Robots)
            {
                continue;
            }

            // No NotEmpty here: an empty <urlset> is a real and correct state — no facet in
            // this fixture clears its threshold, so sitemap-jobfilters is legitimately empty.
            string[] locs = [.. Locs(file.Text)];

            Assert.Equal(locs.Distinct(StringComparer.Ordinal).Count(), locs.Length);
        }

        string[] disallows =
        [
            .. harness.Sink.Live[SitemapNames.Robots].Text
                .Split('\n')
                .Where(l => l.StartsWith("Disallow:", StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(disallows);
        Assert.Equal(disallows.Distinct(StringComparer.Ordinal).Count(), disallows.Length);

        // And specifically the file the defect was found in: every configured static path
        // present, exactly once, in order.
        Assert.Equal(
            ["https://kariyerzamani.com/", $"{Site}/sirketler", $"{Site}/cv"],
            Locs(harness.Sink.Live["sitemap-static-1.xml"].Text));
    }

    private static IEnumerable<string> Locs(string xml) =>
        xml.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("<loc>", StringComparison.Ordinal))
            .Select(line => line["<loc>".Length..^"</loc>".Length]);

    [Fact]
    public async Task NoSitemapEverAdvertisesTheCmsPreviewRoute()
    {
        // /cms-preview is the CMS admin console's live preview: an internal tool on the PUBLIC
        // origin that renders unpublished drafts. Nothing should be able to put it in a sitemap
        // — it is not a cms.seo_page row, so no URL source can produce it today.
        //
        // Asserted anyway, across EVERY file rather than the one that looks likely, because a
        // sitemap is the most effective discovery mechanism there is: a future URL source that
        // started emitting it would hand Googlebot a direct route to unreleased content, and the
        // only signal would be the content appearing in the index.
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/cv-nasil-yazilir");

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        foreach ((string name, StoredFile file) in harness.Sink.Live)
        {
            if (name == SitemapNames.Robots)
            {
                // robots.txt is the one file that SHOULD name it — as a Disallow, never a URL.
                Assert.Contains("Disallow: /cms-preview", file.Text, StringComparison.Ordinal);
                continue;
            }

            Assert.DoesNotContain("cms-preview", file.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OnlyFacetsClearingTheirThresholdReachTheFilterSitemap()
    {
        // Nine jobs in İstanbul/Bilişim: enough for the single-axis city page (≥ 5), NOT
        // enough for the two-axis combo (≥ 10). The gap is the whole point of the test.
        for (int i = 0; i < 9; i++)
        {
            await postgres.SeedJobAsync($"job-{i}", $"slug-{i}",
                province: "İstanbul", department: "Bilişim");
        }

        await using Harness harness = await Harness.StartAsync(postgres, Manifest);

        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        string filters = harness.Sink.Live["sitemap-jobfilters-1.xml"].Text;

        Assert.Contains($"{Site}/is-ilanlari/istanbul<", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("/is-ilanlari/istanbul/yazilim", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("/is-ilanlari/ankara", filters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACrossingFacetEmitsExactlyOneTransitionEvent()
    {
        for (int i = 0; i < 9; i++)
        {
            await postgres.SeedJobAsync($"job-{i}", $"slug-{i}",
                province: "İstanbul", department: "Bilişim");
        }

        await using Harness harness = await Harness.StartAsync(postgres, Manifest);

        await harness.Builder.RebuildAsync("first", CancellationToken.None);

        // The tenth job pushes the combo over its threshold.
        await postgres.SeedJobAsync("job-10", "slug-10", province: "İstanbul", department: "Bilişim");

        await harness.Builder.RebuildAsync("second", CancellationToken.None);

        IReadOnlyList<FacetIndexabilityChangedEvent> transitions =
        [
            .. harness.Bus.Published
                .Select<FacetIndexabilityChangedEvent>()
                .Select(p => p.Context.Message),
        ];

        // One, not one per rebuild. Publishing the state of every candidate on every tick
        // would put a few million pointless messages a month on the exchange and make "a
        // facet became indexable" impossible to alert on.
        FacetIndexabilityChangedEvent crossed = Assert.Single(
            transitions.Where(t => t.FacetPath == "/is-ilanlari/istanbul/yazilim"));

        Assert.True(crossed.Indexable);
        Assert.Equal(10, crossed.JobCount);
    }

    [Fact]
    public async Task AJobThatLeavesTheCorpusWithoutAnEventIsStillRetired()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul");

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("first", CancellationToken.None);

        Assert.Contains("slug-1", harness.Sink.Live["sitemap-jobs-1.xml"].Text, StringComparison.Ordinal);

        // An employer deactivating a posting in the panel. The freshness service publishes
        // nothing for this — it only announces expiries IT performed — so the corpus sync is
        // the ONLY thing that can catch it. Without it the URL would stay advertised forever.
        await postgres.ExecuteAsync("UPDATE public.company_job SET is_active = false WHERE uid = 'job-1'");

        await harness.Builder.RebuildAsync("second", CancellationToken.None);

        Assert.DoesNotContain("slug-1", harness.Sink.Live["sitemap-jobs-1.xml"].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompanyJobIsNeverWritten()
    {
        await postgres.SeedJobAsync("job-1", "slug-1", province: "İstanbul",
            modifiedOn: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using Harness harness = await Harness.StartAsync(postgres);
        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        // PLAN §1: read-only on company_job, always. Asserted against the real table rather
        // than only against RolePlan, because the property that matters is what the SQL did,
        // not what a flag claims.
        string? status = await postgres.ScalarAsync<string>(
            "SELECT status FROM public.company_job WHERE uid = 'job-1'");
        DateTime? modified = await postgres.ScalarAsync<DateTime>(
            "SELECT modified_on FROM public.company_job WHERE uid = 'job-1'");

        Assert.Equal("approved", status);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), modified);
    }

    /// <summary>
    /// A manifest with one single-axis facet and two combos, so the threshold difference is
    /// exercised by the same corpus.
    /// </summary>
    private static IReadOnlyList<FacetDefinition> Manifest =>
    [
        new("/is-ilanlari/istanbul", 1, "İstanbul", [], [], [], []),
        new("/is-ilanlari/istanbul/yazilim", 2, "İstanbul", ["Bilişim"], [], [], []),
        new("/is-ilanlari/ankara", 1, "Ankara", [], [], [], []),
    ];

    /// <summary>
    /// Wires a real store against Testcontainers Postgres, a fake sink and an in-memory bus
    /// harness.
    ///
    /// Real Postgres, fake R2: the SQL is the part that cannot be verified any other way,
    /// while the sink's contract is an ORDERING that a fake can assert directly and MinIO
    /// could only race against.
    /// </summary>
    private sealed class Harness(
        ServiceProvider provider,
        AsyncServiceScope scope,
        SitemapBuilder builder,
        FakeSitemapSink sink,
        ITestHarness bus) : IAsyncDisposable
    {
        public SitemapBuilder Builder => builder;

        public FakeSitemapSink Sink => sink;

        public ITestHarness Bus => bus;

        public static async Task<Harness> StartAsync(
            PostgresFixture postgres, IReadOnlyList<FacetDefinition>? facets = null)
        {
            FakeSitemapSink sink = new();

            ServiceCollection services = [];

            // A fixed clock. Every timestamp the rebuild writes — <lastmod> on the index,
            // generated_at in the log, the event's GeneratedAt — comes from it, so a test can
            // assert on exact bytes rather than on "something that looks like a date".
            services.AddSingleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)));

            services.AddLogging();
            services.AddSingleton<ISitemapSink>(sink);
            services.AddSingleton<IFacetManifestSource>(new StaticManifest(
                facets ?? [new("/is-ilanlari/istanbul", 1, "İstanbul", [], [], [], [])]));

            // Compression off, so the fake sink's stored text is the XML itself and an
            // assertion failure is readable rather than a hex dump.
            services.AddSingleton(Options.Create(new SeoOptions
            {
                SiteUrl = Site,

                // Stated, because SeoOptions no longer carries these as C# defaults — a
                // non-empty initialiser is what made the configuration binder APPEND to them
                // and publish every entry twice. Configuration is the only source now, and a
                // hand-built options object is configuration. That the SHIPPED appsettings.json
                // still carries these values is asserted separately, in
                // ConfigurationBindingTests.
                StaticPaths = ["/", "/sirketler", "/cv"],
                DisallowedPaths = ["/api/", "/hesabim", "/cms-preview"],

                R2 = new R2Options { Compress = false },
            }));

            services.AddSingleton(Options.Create(new PersistenceOptions()));

            services.AddDbContext<SeoDbContext>(o => o.UseNpgsql(postgres.ConnectionString));
            services.AddScoped<ISeoStore, SeoStore>();
            services.AddScoped<SitemapBuilder>();

            services.AddMassTransitTestHarness();

            ServiceProvider provider = services.BuildServiceProvider();

            ITestHarness bus = provider.GetRequiredService<ITestHarness>();
            await bus.Start();

            AsyncServiceScope scope = provider.CreateAsyncScope();

            return new Harness(
                provider,
                scope,
                scope.ServiceProvider.GetRequiredService<SitemapBuilder>(),
                sink,
                bus);
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class StaticManifest(IReadOnlyList<FacetDefinition> facets) : IFacetManifestSource
    {
        public Task<IReadOnlyList<FacetDefinition>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(facets);
    }
}
