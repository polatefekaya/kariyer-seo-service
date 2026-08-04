using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Storage;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kariyer.Seo.IntegrationTests.Storage;

/// <summary>
/// What actually lands in the bucket, with the REAL <see cref="SitemapSink"/> behind both
/// write paths and compression on — the production configuration.
///
/// <b>Why this exists.</b> Every other sitemap suite substitutes <c>ISitemapSink</c> for
/// <c>FakeSitemapSink</c>, whose streams are deliberately non-closing so the bytes survive
/// for an assertion. That is the right fake for asserting ORDERING, and it is the reason a
/// total outage went unnoticed: the sink's own stream ownership was never exercised.
/// <c>CommitAsync</c> flushed each registered stream before disposing it, but the caller owns
/// that stream and had already disposed it, so every commit threw and every staged set was
/// discarded. Health stayed green, the cron loop kept going, and the only symptom was a
/// bucket that never filled.
///
/// So this suite runs <see cref="SitemapBuilder"/> and <see cref="JobSitemapProjector"/>
/// unchanged, over real Postgres, into the real sink, and looks at the objects.
///
/// <b>Why the assertions decompress.</b> The other half of the ownership question is the gzip
/// trailer, and a truncated archive is a corrupt sitemap served with a 200 — a failure
/// nothing downstream reports. Every published object is therefore decompressed and its
/// trailer checked, not merely counted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class R2SitemapPublicationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Site = "https://kariyerzamani.com";
    private const string Prefix = "sitemaps/";

    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>The whole set, at the keys a crawler fetches. Spelled out, not computed.</summary>
    private static readonly string[] LiveKeys =
    [
        Prefix + "sitemap.xml.gz",
        Prefix + "sitemap-jobs-1.xml.gz",
        Prefix + "sitemap-jobfilters-1.xml.gz",
        Prefix + "sitemap-pages-1.xml.gz",
        Prefix + "sitemap-static-1.xml.gz",

        // No .gz: StoredName only suffixes .xml, so robots.txt keeps the name every crawler
        // on the internet has hard-coded and declares its compression in the header alone.
        Prefix + "robots.txt",
    ];

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AFullRebuildPublishesEveryFileAtItsLiveKey()
    {
        await SeedCorpusAsync();

        await using Harness harness = await Harness.StartAsync(postgres);

        // Before the fix this threw ObjectDisposedException out of CommitAsync and the bucket
        // stayed empty — on every tick, for every deployment.
        RebuildOutcome outcome = await harness.Builder.RebuildAsync("test", CancellationToken.None);

        Assert.Equal(6, outcome.Files);

        IReadOnlyDictionary<string, FakeS3Bucket.StoredObject> objects = harness.Bucket.Objects;

        foreach (string key in LiveKeys)
        {
            Assert.Contains(key, objects);
        }

        // Nothing left under the staging prefix. Those objects are inert — nothing routes
        // there — but a sweep that stopped working would grow a second copy of the corpus in
        // the bucket indefinitely, and nothing else would ever say so.
        Assert.DoesNotContain(
            objects.Keys, k => k.StartsWith(Prefix + "_staging/", StringComparison.Ordinal));

        // Exactly the set, no extras.
        Assert.Equal(LiveKeys.Order(StringComparer.Ordinal), objects.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryPublishedXmlObjectGunzipsToAWellFormedSitemapDocument()
    {
        await SeedCorpusAsync();

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        IReadOnlyDictionary<string, FakeS3Bucket.StoredObject> objects = harness.Bucket.Objects;

        foreach (string key in LiveKeys.Where(k => k.EndsWith(".xml.gz", StringComparison.Ordinal)))
        {
            FakeS3Bucket.StoredObject stored = objects[key];

            // Content-Encoding, not a content type of its own: Google fetches the .gz and
            // expects XML inside a gzip envelope. application/gzip would make some clients
            // treat it as an opaque download instead of decoding it.
            Assert.Equal("gzip", stored.ContentEncoding);
            Assert.Equal("application/xml", stored.ContentType);
            Assert.Equal("public, max-age=600", stored.CacheControl);

            // Fails on a missing or wrong gzip trailer — see Gunzip. This is the assertion
            // that separates "the commit did not throw" from "the object is readable".
            XDocument document = XDocument.Parse(Encoding.UTF8.GetString(Gunzip(stored.Body)));

            Assert.Equal(
                key == Prefix + "sitemap.xml.gz" ? Sitemap + "sitemapindex" : Sitemap + "urlset",
                document.Root!.Name);
        }

        // The documents carry the corpus, not merely a well-formed shell of it. A gzip stream
        // that was flushed but never closed decompresses to a SHORT document just as readily
        // as to an invalid archive.
        XDocument jobs = Parse(objects[Prefix + "sitemap-jobs-1.xml.gz"]);

        Assert.Contains(
            $"{Site}/is-ilanlari/ilan/yazilim-muhendisi-1",
            jobs.Descendants(Sitemap + "loc").Select(e => e.Value));

        XDocument pages = Parse(objects[Prefix + "sitemap-pages-1.xml.gz"]);

        Assert.Contains($"{Site}/kariyer-rehberi", pages.Descendants(Sitemap + "loc").Select(e => e.Value));

        // robots.txt is gzipped too, and a truncated one is a de-indexing event rather than a
        // missing file — so it gets the same trailer check.
        string robots = Encoding.UTF8.GetString(Gunzip(objects[Prefix + "robots.txt"].Body));

        Assert.Contains($"Sitemap: {Site}/sitemap.xml", robots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveIndexOnlyNamesObjectsThatArePublished()
    {
        await SeedCorpusAsync();

        await using Harness harness = await Harness.StartAsync(postgres);

        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        IReadOnlyDictionary<string, FakeS3Bucket.StoredObject> objects = harness.Bucket.Objects;

        XDocument index = Parse(objects[Prefix + "sitemap.xml.gz"]);

        string[] children = [.. index.Descendants(Sitemap + "loc").Select(e => e.Value)];

        Assert.NotEmpty(children);

        foreach (string loc in children)
        {
            // The index names the STORED name, .gz suffix and all, because that is the URL a
            // crawler will request. SitemapBuilder computes that suffix and the sink computes
            // it again independently; this is the only place the two are checked against each
            // other, and a disagreement would publish an index of 404s.
            string key = Prefix + loc[(loc.LastIndexOf('/') + 1)..];

            Assert.Contains(key, objects);
        }
    }

    [Fact]
    public async Task ADirtyFlushPublishesTheJobsFileAndTheIndex()
    {
        // Two jobs in the corpus and both projected, one of them since removed and flagged.
        // Seeded straight into the tables rather than driven through the bus: the consumer is
        // FreshnessConsumerTests' subject, and what is under test here is the sink.
        await postgres.SeedJobAsync("job-1", "yazilim-muhendisi-1");
        await postgres.SeedJobAsync("job-2", "grafik-tasarimci-2");

        await SeedProjectionAsync("job-1", "yazilim-muhendisi-1", SeoUrlStatus.Live);
        await SeedProjectionAsync("job-2", "grafik-tasarimci-2", SeoUrlStatus.Removed, dirty: true);

        await using Harness harness = await Harness.StartAsync(postgres);

        // The reactor role's write path. It stages through the same sink and committed just
        // as reliably as the builder's did — which is to say, never, before the fix.
        await harness.Projector.FlushAsync(CancellationToken.None);

        IReadOnlyDictionary<string, FakeS3Bucket.StoredObject> objects = harness.Bucket.Objects;

        Assert.Contains(Prefix + "sitemap-jobs-1.xml.gz", objects);
        Assert.Contains(Prefix + "sitemap-pages-1.xml.gz", objects);

        // The index is rewritten alongside, so its <lastmod> for the file that just changed
        // does not advertise a stale timestamp.
        Assert.Contains(Prefix + "sitemap.xml.gz", objects);

        XDocument jobs = Parse(objects[Prefix + "sitemap-jobs-1.xml.gz"]);

        string[] locs = [.. jobs.Descendants(Sitemap + "loc").Select(e => e.Value)];

        Assert.Contains($"{Site}/is-ilanlari/ilan/yazilim-muhendisi-1", locs);
        Assert.DoesNotContain($"{Site}/is-ilanlari/ilan/grafik-tasarimci-2", locs);
    }

    [Fact]
    public async Task WithCompressionOffTheBucketHoldsPlainXmlAtUnsuffixedKeys()
    {
        await SeedCorpusAsync();

        await using Harness harness = await Harness.StartAsync(postgres, compress: false);

        await harness.Builder.RebuildAsync("test", CancellationToken.None);

        IReadOnlyDictionary<string, FakeS3Bucket.StoredObject> objects = harness.Bucket.Objects;

        // With compression off OpenWrite returns the temp FileStream itself, so the caller
        // disposes the very stream the sink still has to read from. Same ownership question,
        // one layer down, and it has to come out the same way.
        Assert.DoesNotContain(objects.Keys, k => k.EndsWith(".gz", StringComparison.Ordinal));
        Assert.Contains(Prefix + "sitemap.xml", objects);
        Assert.Contains(Prefix + "sitemap-jobs-1.xml", objects);

        foreach ((string key, FakeS3Bucket.StoredObject stored) in objects)
        {
            Assert.Null(stored.ContentEncoding);

            if (key.EndsWith(".xml", StringComparison.Ordinal))
            {
                XDocument.Parse(Encoding.UTF8.GetString(stored.Body));
            }
        }

        // And the index advertises the unsuffixed names, so the two independent copies of the
        // suffix rule agree in this direction too.
        XDocument index = XDocument.Parse(
            Encoding.UTF8.GetString(objects[Prefix + "sitemap.xml"].Body));

        Assert.DoesNotContain(
            index.Descendants(Sitemap + "loc").Select(e => e.Value),
            loc => loc.EndsWith(".gz", StringComparison.Ordinal));
    }

    private Task SeedCorpusAsync() =>
        Task.WhenAll(
            postgres.SeedJobAsync("job-1", "yazilim-muhendisi-1",
                province: "İstanbul", department: "Bilişim",
                modifiedOn: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero)),
            postgres.SeedJobAsync("job-2", "grafik-tasarimci-2",
                province: "İstanbul", department: "Bilişim"),
            postgres.SeedCmsPageAsync("/kariyer-rehberi",
                publishedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));

    private Task SeedProjectionAsync(string uid, string slug, SeoUrlStatus status, bool dirty = false) =>
        postgres.ExecuteAsync($"""
            INSERT INTO seo.seo_url_state (job_uid, slug, status, last_modified, dirty, updated_at)
            VALUES ('{uid}', '{slug}', {(int)status}, NULL, {dirty.ToString().ToLowerInvariant()}, now())
            """);

    private static XDocument Parse(FakeS3Bucket.StoredObject stored) =>
        XDocument.Parse(Encoding.UTF8.GetString(Gunzip(stored.Body)));

    /// <summary>
    /// Decompresses, and checks the gzip TRAILER by hand.
    ///
    /// The hand-rolled part is the point. A <see cref="GZipStream"/> that was flushed but
    /// never disposed has emitted all of its compressed data and none of its 8-byte
    /// CRC32+ISIZE footer, and .NET's decompressor is lenient about precisely that: it
    /// returns what it found and reports end-of-stream rather than throwing. <c>CopyTo</c>
    /// alone therefore "succeeds" on a truncated archive that <c>gunzip</c> rejects with
    /// "unexpected end of file" — which is what a crawler's decompressor would do.
    ///
    /// Checked against the footer instead: decompressor-agnostic, and exact.
    /// </summary>
    private static byte[] Gunzip(byte[] body)
    {
        using MemoryStream compressed = new(body);
        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream buffer = new();

        gzip.CopyTo(buffer);

        byte[] plain = buffer.ToArray();

        // A gzip member is at least a 10-byte header and an 8-byte trailer.
        Assert.True(body.Length >= 18, "The object is too short to be a gzip archive at all.");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 8, 4));
        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 4, 4));

        Assert.Equal((uint)plain.Length, storedSize);
        Assert.Equal(Crc32(plain), storedCrc);

        return plain;
    }

    /// <summary>
    /// CRC-32 with the reflected zlib polynomial, which is what gzip stores. Hand-rolled
    /// because the BCL's only implementation is in the System.IO.Hashing package, and taking
    /// a dependency to check eight bytes is the worse trade.
    /// </summary>
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private sealed class Harness(
        ServiceProvider provider,
        AsyncServiceScope scope,
        SitemapBuilder builder,
        JobSitemapProjector projector,
        FakeS3Bucket bucket) : IAsyncDisposable
    {
        public SitemapBuilder Builder => builder;

        public JobSitemapProjector Projector => projector;

        public FakeS3Bucket Bucket => bucket;

        public static async Task<Harness> StartAsync(PostgresFixture postgres, bool compress = true)
        {
            FakeS3Bucket bucket = new();

            ServiceCollection services = [];

            services.AddSingleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)));

            services.AddLogging();

            // The real sink, over the fake bucket. This is the whole point of the suite —
            // every sibling harness registers FakeSitemapSink here instead.
            services.AddSingleton(bucket.Client);
            services.AddSingleton<ISitemapSink, SitemapSink>();

            services.AddSingleton<IFacetManifestSource>(new StaticManifest(
                [new("/is-ilanlari/istanbul", 1, "İstanbul", [], [], [], [])]));

            services.AddSingleton(Options.Create(new SeoOptions
            {
                SiteUrl = Site,
                R2 = new R2Options
                {
                    Bucket = "kariyer-seo-test",

                    // Non-empty deliberately. The default is "", which makes the live key
                    // identical to the stored name and would hide any prefix bug in either
                    // the sink's key building or its ToFileName strip.
                    Prefix = Prefix,
                    StagingPrefix = "_staging/",

                    // On by default here, unlike every sibling harness — this is the
                    // production setting, and it is the configuration the outage happened in.
                    Compress = compress,
                },

                // CacheControl left at its default, so the assertion pins what actually ships.
            }));

            services.AddSingleton(Options.Create(new PersistenceOptions()));

            services.AddDbContext<SeoDbContext>(o => o.UseNpgsql(postgres.ConnectionString));
            services.AddScoped<ISeoStore, SeoStore>();
            services.AddScoped<SitemapBuilder>();
            services.AddScoped<JobSitemapProjector>();

            services.AddMassTransitTestHarness();

            ServiceProvider provider = services.BuildServiceProvider();

            ITestHarness bus = provider.GetRequiredService<ITestHarness>();
            await bus.Start();

            AsyncServiceScope scope = provider.CreateAsyncScope();

            return new Harness(
                provider,
                scope,
                scope.ServiceProvider.GetRequiredService<SitemapBuilder>(),
                scope.ServiceProvider.GetRequiredService<JobSitemapProjector>(),
                bucket);
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
