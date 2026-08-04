using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Storage;
using Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kariyer.Seo.IntegrationTests.Storage;

/// <summary>
/// A full rebuild against a REAL Cloudflare R2 bucket. Skipped unless credentials are in the
/// environment (see <see cref="RequiresR2FactAttribute"/>).
///
/// <b>Why this has to exist, and why it cannot run in CI.</b> Two production outages in a row
/// were invisible to every other suite, and the second one is the reason this file is here.
/// The AWS SDK defaults <c>PutObjectRequest.UseChunkEncoding</c> to true, which signs the body
/// as a streaming chunked payload — <c>x-amz-content-sha256:
/// STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>. R2 does not implement that mode and rejects the
/// request outright, so no upload ever succeeded.
///
/// Nothing automatable catches it:
///
/// <list type="bullet">
///   <item><c>FakeS3Bucket</c> is in-process and never signs a request, so it passes
///   regardless of what the signing configuration says;</item>
///   <item>MinIO — the dev stand-in in docker-compose.Development.yml — DOES implement the
///   streaming mode. Verified against minio/minio:latest rather than assumed: it accepts the
///   upload with chunk encoding on and with it off. A MinIO-backed test would therefore have
///   stayed green through the entire outage, which is worse than no test, because it would
///   have been read as evidence the upload path worked.</item>
/// </list>
///
/// So the wire behaviour is only observable against R2 itself. This is the one way to check
/// it before deploying, and it is deliberately opt-in: it needs credentials CI does not have,
/// and it WRITES TO A REAL BUCKET. Everything it writes goes under a per-run unique prefix
/// and is deleted afterwards, so it cannot touch a live sitemap set even when pointed at the
/// production bucket.
///
/// <code>
/// export SEO_R2_ENDPOINT=https://&lt;account&gt;.r2.cloudflarestorage.com
/// export SEO_R2_BUCKET=kariyer-seo
/// export SEO_R2_ACCESS_KEY=… SEO_R2_SECRET_KEY=…
/// dotnet test tests/Kariyer.Seo.IntegrationTests --filter FullyQualifiedName~R2SmokeTests
/// </code>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class R2SmokeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Site = "https://kariyerzamani.com";

    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>
    /// A prefix of this run's own, so a smoke test can be pointed at the production bucket
    /// without any chance of colliding with — or sweeping — the live set. The sink deletes
    /// obsolete files inside its own prefix, and that prefix is this one.
    /// </summary>
    private readonly string _prefix = $"seo-smoke/{Guid.NewGuid():N}/";

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresR2Fact]
    public async Task AFullRebuildPublishesAReadableSitemapSetToARealBucket()
    {
        R2Options r2 = R2FromEnvironment(_prefix);

        await postgres.SeedJobAsync("job-1", "yazilim-muhendisi-1",
            province: "İstanbul", department: "Bilişim",
            modifiedOn: new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        await postgres.SeedJobAsync("job-2", "grafik-tasarimci-2",
            province: "İstanbul", department: "Bilişim");
        await postgres.SeedCmsPageAsync("/kariyer-rehberi",
            publishedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        using AmazonS3Client s3 = CreateClient(r2);

        try
        {
            await using Harness harness = await Harness.StartAsync(postgres, r2, s3);

            // The whole point. Against a real bucket with chunk encoding left on, this throws
            // AmazonS3Exception "STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented" out of
            // PutAsync and the stage is discarded.
            RebuildOutcome outcome = await harness.Builder.RebuildAsync("smoke", CancellationToken.None);

            Assert.Equal(6, outcome.Files);

            // ── 1. The set is live, at the keys a crawler fetches ───────────────────
            IReadOnlyList<string> keys = await ListAsync(s3, r2.Bucket, r2.Prefix);

            string[] expected =
            [
                "sitemap.xml.gz",
                "sitemap-jobs-1.xml.gz",
                "sitemap-jobfilters-1.xml.gz",
                "sitemap-pages-1.xml.gz",
                "sitemap-static-1.xml.gz",
                "robots.txt",
            ];

            foreach (string name in expected)
            {
                Assert.Contains(r2.Prefix + name, keys);
            }

            // ── 2. The promote ran and staging was swept ────────────────────────────
            //
            // Unverified against real R2 until now: the promote is a server-side CopyObject
            // and had never once executed, because every upload before it failed.
            Assert.DoesNotContain(
                keys, k => k.StartsWith(r2.Prefix + r2.StagingPrefix, StringComparison.Ordinal));

            // ── 3. Every .xml.gz is a valid archive of well-formed XML ──────────────
            foreach (string name in expected.Where(n => n.EndsWith(".xml.gz", StringComparison.Ordinal)))
            {
                GetObjectResponse stored = await s3.GetObjectAsync(r2.Bucket, r2.Prefix + name);

                // ── 4. …and the headers and metadata survived the round trip ────────
                //
                // Asserted on the LIVE object, not the staged one: the live copy is produced
                // by CopyObject with MetadataDirective.COPY, so this is also the check that
                // the promote carries everything rather than resetting it.
                Assert.Equal("gzip", stored.Headers.ContentEncoding);
                Assert.Equal("application/xml", stored.Headers.ContentType);
                Assert.Equal("public, max-age=600", stored.Headers.CacheControl);
                Assert.False(
                    string.IsNullOrEmpty(stored.Metadata[SitemapSink.ChecksumMetadataKey]),
                    $"{name} reached the bucket without its seo-checksum metadata, which is "
                    + "what the conditional-write short-circuit reads on the next run.");

                byte[] body = await ReadAllAsync(stored);

                XDocument document = XDocument.Parse(Encoding.UTF8.GetString(Gunzip(body)));

                Assert.Equal(
                    name == "sitemap.xml.gz" ? Sitemap + "sitemapindex" : Sitemap + "urlset",
                    document.Root!.Name);
            }

            // The index names only objects that are actually there. A crawler does exactly
            // this walk, and a 404 here is the torn set the stage-and-swap exists to prevent.
            GetObjectResponse index = await s3.GetObjectAsync(r2.Bucket, r2.Prefix + "sitemap.xml.gz");

            XDocument indexDocument = XDocument.Parse(
                Encoding.UTF8.GetString(Gunzip(await ReadAllAsync(index))));

            foreach (string loc in indexDocument.Descendants(Sitemap + "loc").Select(e => e.Value))
            {
                Assert.Contains(r2.Prefix + loc[(loc.LastIndexOf('/') + 1)..], keys);
            }

            // robots.txt is gzipped too but stored without a .gz suffix.
            GetObjectResponse robots = await s3.GetObjectAsync(r2.Bucket, r2.Prefix + "robots.txt");

            Assert.Equal("gzip", robots.Headers.ContentEncoding);

            Assert.Contains(
                $"Sitemap: {Site}/sitemap.xml",
                Encoding.UTF8.GetString(Gunzip(await ReadAllAsync(robots))),
                StringComparison.Ordinal);
        }
        finally
        {
            await CleanUpAsync(s3, r2);
        }
    }

    /// <summary>
    /// Removes everything this run wrote. In a finally, because a bucket that accumulates a
    /// dead sitemap set per smoke run is a mess someone else has to clean up by hand.
    /// </summary>
    private static async Task CleanUpAsync(AmazonS3Client s3, R2Options r2)
    {
        try
        {
            foreach (string key in await ListAsync(s3, r2.Bucket, r2.Prefix))
            {
                await s3.DeleteObjectAsync(r2.Bucket, key);
            }
        }
        catch (AmazonS3Exception)
        {
            // Best effort. A leaked smoke prefix is untidy; failing the test for it would
            // hide whatever the assertions above actually found.
        }
    }

    private static async Task<IReadOnlyList<string>> ListAsync(
        AmazonS3Client s3, string bucket, string prefix)
    {
        List<string> keys = [];

        ListObjectsV2Request request = new() { BucketName = bucket, Prefix = prefix, MaxKeys = 1000 };
        ListObjectsV2Response response;

        do
        {
            response = await s3.ListObjectsV2Async(request);

            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key));

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated);

        return keys;
    }

    private static async Task<byte[]> ReadAllAsync(GetObjectResponse response)
    {
        using MemoryStream buffer = new();

        // ResponseStream, not Open — the SDK does not decompress for us, and this test wants
        // the stored bytes exactly as a crawler would receive them.
        await response.ResponseStream.CopyToAsync(buffer);

        return buffer.ToArray();
    }

    private static R2Options R2FromEnvironment(string prefix) => new()
    {
        Endpoint = Environment.GetEnvironmentVariable("SEO_R2_ENDPOINT")!,
        Bucket = Environment.GetEnvironmentVariable("SEO_R2_BUCKET")!,
        AccessKey = Environment.GetEnvironmentVariable("SEO_R2_ACCESS_KEY")!,
        SecretKey = Environment.GetEnvironmentVariable("SEO_R2_SECRET_KEY")!,
        Prefix = prefix,
        StagingPrefix = "_staging/",
        Compress = true,
    };

    /// <summary>
    /// The same client configuration the Worker builds in
    /// <see cref="StorageExtensions.AddSitemapSink"/>. Duplicated rather than resolved through
    /// DI so that a change there which breaks R2 shows up here as a difference to reconcile
    /// rather than being silently inherited.
    /// </summary>
    private static AmazonS3Client CreateClient(R2Options r2) => new(
        new BasicAWSCredentials(r2.AccessKey, r2.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = r2.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
        });

    /// <summary>
    /// Decompresses and verifies the gzip trailer by hand — .NET's decompressor returns the
    /// flushed bytes and reports end-of-stream on a truncated archive that <c>gunzip</c>
    /// rejects, so CopyTo alone is not the check it looks like. See SitemapSinkTests.
    /// </summary>
    private static byte[] Gunzip(byte[] body)
    {
        using MemoryStream compressed = new(body);
        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream buffer = new();

        gzip.CopyTo(buffer);

        byte[] plain = buffer.ToArray();

        Assert.True(body.Length >= 18, "The object is too short to be a gzip archive at all.");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 8, 4));
        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 4, 4));

        Assert.Equal((uint)plain.Length, storedSize);
        Assert.Equal(Crc32(plain), storedCrc);

        return plain;
    }

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
        ServiceProvider provider, AsyncServiceScope scope, SitemapBuilder builder) : IAsyncDisposable
    {
        public SitemapBuilder Builder => builder;

        public static async Task<Harness> StartAsync(
            PostgresFixture postgres, R2Options r2, IAmazonS3 s3)
        {
            ServiceCollection services = [];

            services.AddSingleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)));

            services.AddLogging();

            services.AddSingleton(s3);
            services.AddSingleton<ISitemapSink, SitemapSink>();

            services.AddSingleton<IFacetManifestSource>(new StaticManifest(
                [new("/is-ilanlari/istanbul", 1, "İstanbul", [], [], [], [])]));

            services.AddSingleton(Options.Create(new SeoOptions { SiteUrl = Site, R2 = r2 }));
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
                provider, scope, scope.ServiceProvider.GetRequiredService<SitemapBuilder>());
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

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless real R2 credentials are present.
///
/// A plain <c>[Fact(Skip = …)]</c> can never run, and a test that silently PASSES when it did
/// nothing is worse — it reports success for a check that was not performed. Skipping is
/// visible in the run summary, which is the honest answer to "was the bucket verified".
/// </summary>
internal sealed class RequiresR2FactAttribute : FactAttribute
{
    private static readonly string[] Required =
    [
        "SEO_R2_ENDPOINT", "SEO_R2_BUCKET", "SEO_R2_ACCESS_KEY", "SEO_R2_SECRET_KEY",
    ];

    public RequiresR2FactAttribute()
    {
        string[] missing =
        [
            .. Required.Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v))),
        ];

        if (missing.Length > 0)
        {
            Skip = "Needs a real R2 bucket; set " + string.Join(", ", missing)
                + ". This is the only check that exercises SigV4 against R2 — neither the "
                + "in-process fake nor MinIO can reproduce the signing failures it guards.";
        }
    }
}
