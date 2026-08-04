using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Robots;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Kariyer.Seo.Worker.Tests.Storage;

/// <summary>
/// What the sink actually puts in the bucket, driven the way its real callers drive it.
///
/// <b>Why this exists.</b> Every other sitemap test substitutes <c>ISitemapSink</c> for an
/// in-memory fake whose streams are deliberately non-closing, so nothing exercised the real
/// stream ownership at all. That gap hid a total outage: <c>CommitAsync</c> used to
/// <c>FlushAsync</c> each registered stream before disposing it, but the CALLER owns the
/// stream <c>OpenWrite</c> returns and has already disposed it — <c>SitemapWriter</c> says so
/// in its contract — and flushing a disposed stream throws. Every commit the service ever
/// attempted was discarded, the cron loop swallowed the exception, and both health endpoints
/// stayed green. The only symptom was a bucket that never filled.
///
/// <b>Why the assertions decompress and parse rather than count calls.</b> The other half of
/// that ownership question is the gzip trailer. A <c>GZipStream</c> that is not disposed has
/// not written its 8-byte footer, so the object uploads as a truncated archive: a corrupt
/// sitemap served with a 200, which no crawler reports back and no metric here would show. A
/// test that asserted only "the commit did not throw" would pass against that. So every
/// uploaded object is decompressed in full and parsed.
///
/// These run against a substituted <see cref="IAmazonS3"/> and no container, so they are the
/// fast guard; <c>R2SitemapPublicationTests</c> covers the same ground through the real
/// callers over Postgres.
/// </summary>
public sealed class SitemapSinkTests
{
    private const string Site = "https://kariyerzamani.com";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    [Fact]
    public async Task ACommittedStageUploadsEveryFileTheCallerAlreadyClosed()
    {
        Fixture fixture = new(compress: true);

        await using (ISitemapStage stage = await fixture.Sink.BeginAsync(CancellationToken.None))
        {
            fixture.WriteUrlSet(stage, SitemapNames.JobsBase, "yazilim-muhendisi-1");
            await fixture.WriteIndexAndRobotsAsync(stage);

            // The regression. Before the fix this line threw
            // ObjectDisposedException('System.IO.Compression.GZipStream') from CommitAsync's
            // pre-flush, because every stream OpenWrite handed out had already been disposed
            // by its caller — which is the documented contract, not a caller bug.
            //
            // It guards the other direction too: drop the dispose loop from CommitAsync
            // instead of the flush, and PutAsync's File.OpenRead hits the temp file's still
            // open FileShare.None handle and throws IOException here.
            await stage.CommitAsync(SitemapNames.Index, [], CancellationToken.None);
        }

        // Three files staged, three objects PUT. Zero would mean the commit aborted before
        // the first upload, which is precisely how this failed in production.
        Assert.Equal(3, fixture.Puts.Count);
    }

    [Fact]
    public async Task EveryUploadedXmlObjectIsAValidGzipArchiveOfExactlyWhatWasWritten()
    {
        Fixture fixture = new(compress: true);

        await using (ISitemapStage stage = await fixture.Sink.BeginAsync(CancellationToken.None))
        {
            fixture.WriteUrlSet(stage, SitemapNames.JobsBase, "yazilim-muhendisi-1", "grafik-tasarimci-2");
            await fixture.WriteIndexAndRobotsAsync(stage);

            await stage.CommitAsync(SitemapNames.Index, [], CancellationToken.None);
        }

        foreach (StoredObject put in fixture.Puts.Where(p => p.Key.EndsWith(".gz", StringComparison.Ordinal)))
        {
            // A crawler fetches `sitemap-jobs-1.xml.gz` and expects XML inside a gzip
            // envelope, so the encoding rides in the header and not in the content type.
            Assert.Equal("gzip", put.ContentEncoding);
            Assert.Equal("application/xml", put.ContentType);

            // Fails on a missing or wrong trailer — see Gunzip. A commit that uploads a
            // truncated archive succeeds loudly and fails silently everywhere else.
            byte[] plain = Gunzip(put.Body);

            XDocument document = XDocument.Parse(Encoding.UTF8.GetString(plain));

            Assert.Equal(
                put.Key.Contains("sitemap.xml", StringComparison.Ordinal)
                    ? Sitemap + "sitemapindex"
                    : Sitemap + "urlset",
                document.Root!.Name);
        }

        // The envelope is an encoding and nothing else — the document inside it is the one the
        // writer streamed in, not a plausible-looking prefix of it. A gzip stream flushed but
        // never closed decompresses to a SHORT document as well as an invalid archive, so the
        // content is checked as well as the framing.
        StoredObject jobs = fixture.Puts.Single(p => p.Key.Contains(SitemapNames.JobsBase, StringComparison.Ordinal));

        Assert.Contains(
            "/is-ilanlari/ilan/yazilim-muhendisi-1",
            Encoding.UTF8.GetString(Gunzip(jobs.Body)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoUploadUsesChunkedPayloadSigningWhichR2RejectsOutright()
    {
        Fixture fixture = new(compress: true);

        await using (ISitemapStage stage = await fixture.Sink.BeginAsync(CancellationToken.None))
        {
            fixture.WriteUrlSet(stage, SitemapNames.JobsBase, "yazilim-muhendisi-1");
            await fixture.WriteIndexAndRobotsAsync(stage);

            await stage.CommitAsync(SitemapNames.Index, [], CancellationToken.None);
        }

        Assert.NotEmpty(fixture.Puts);

        // The SDK defaults UseChunkEncoding to true, which signs the body as a streaming
        // chunked payload: `x-amz-content-sha256: STREAMING-AWS4-HMAC-SHA256-PAYLOAD`, with
        // chunk framing around the bytes. Cloudflare R2 does not implement that mode and
        // rejects the request with "STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented", so
        // with it left on this service cannot upload anything at all.
        //
        // This assertion exists because NOTHING ELSE CAN CATCH IT. The fake bucket is
        // in-process and never signs a request; MinIO, the dev stand-in, does implement the
        // streaming mode and accepts the upload either way — verified, not assumed. The
        // failure appears only against a real R2 bucket, which no automated test here
        // reaches. So the flag is pinned here, and R2SmokeTests covers it for real when
        // credentials are supplied.
        Assert.All(fixture.Puts, put => Assert.False(put.UseChunkEncoding));
    }

    [Fact]
    public async Task RobotsTxtIsNeverCompressedEvenWhenTheXmlIs()
    {
        Fixture fixture = new(compress: true);

        await using (ISitemapStage stage = await fixture.Sink.BeginAsync(CancellationToken.None))
        {
            await fixture.WriteRobotsAsync(stage);

            await stage.CommitAsync(SitemapNames.Index, [], CancellationToken.None);
        }

        StoredObject robots = Assert.Single(fixture.Puts);

        // robots.txt is the one file whose URL is fixed — every crawler fetches /robots.txt
        // exactly — so it can never take a .gz suffix. It used to be gzipped anyway and
        // stored under the suffix-less key with Content-Encoding: gzip, because OpenWrite
        // decided what to compress and StoredName decided what to rename, separately. Anything
        // fetching without announcing gzip support got binary. One predicate now answers both.
        Assert.Equal("sitemaps/_staging/robots.txt", robots.Key);
        Assert.Null(robots.ContentEncoding);
        Assert.Equal("text/plain", robots.ContentType);

        // Plain text, readable without decompressing anything.
        Assert.Contains(
            "Sitemap: https://kariyerzamani.com/sitemap.xml",
            Encoding.UTF8.GetString(robots.Body),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithCompressionOffTheObjectIsThePlainDocument()
    {
        Fixture fixture = new(compress: false);

        await using (ISitemapStage stage = await fixture.Sink.BeginAsync(CancellationToken.None))
        {
            fixture.WriteUrlSet(stage, SitemapNames.JobsBase, "yazilim-muhendisi-1");

            await stage.CommitAsync(SitemapNames.Index, [], CancellationToken.None);
        }

        StoredObject jobs = Assert.Single(fixture.Puts);

        // No envelope, no suffix, no encoding header. With Compress=false OpenWrite returns
        // the temp FileStream itself, so the caller disposes the very stream the sink still
        // needs to read — the same ownership question as the gzip case, one layer down.
        Assert.Equal("sitemaps/_staging/sitemap-jobs-1.xml", jobs.Key);
        Assert.Null(jobs.ContentEncoding);
        Assert.Equal("public, max-age=600", jobs.CacheControl);

        XDocument document = XDocument.Parse(Encoding.UTF8.GetString(jobs.Body));

        Assert.Equal(Sitemap + "urlset", document.Root!.Name);
        Assert.Single(document.Root.Elements(Sitemap + "url"));
    }

    /// <summary>
    /// Decompresses, and checks the gzip TRAILER by hand.
    ///
    /// The hand-rolled part is not paranoia, it is the whole point. A <see cref="GZipStream"/>
    /// that was flushed but never disposed has emitted all of its compressed data and none of
    /// its 8-byte CRC32+ISIZE footer — and .NET's decompressor is lenient about exactly that
    /// case: it returns the bytes it found and reports end-of-stream rather than throwing. So
    /// `CopyTo` alone happily "succeeds" on a truncated archive that <c>gunzip</c> rejects
    /// with "unexpected end of file", which is what an actual crawler's decompressor would do.
    ///
    /// Verified against the footer instead, which is decompressor-agnostic and exact: ISIZE
    /// must be the uncompressed length and CRC32 must be its checksum. That is the assertion
    /// a "no exception was thrown" test cannot make.
    /// </summary>
    private static byte[] Gunzip(byte[] body)
    {
        using MemoryStream compressed = new(body);
        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream buffer = new();

        gzip.CopyTo(buffer);

        byte[] plain = buffer.ToArray();

        // 10-byte header + 8-byte trailer is the floor for a gzip member.
        Assert.True(body.Length >= 18, "The object is too short to be a gzip archive at all.");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 8, 4));
        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 4, 4));

        Assert.Equal((uint)plain.Length, storedSize);
        Assert.Equal(Crc32(plain), storedCrc);

        return plain;
    }

    /// <summary>
    /// CRC-32 (the reflected zlib polynomial gzip uses). Hand-rolled because the BCL's only
    /// implementation lives in the System.IO.Hashing package, and a new dependency to check
    /// eight bytes is a worse trade than fifteen lines here.
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

    /// <summary>One object as it reached the bucket.</summary>
    private sealed record StoredObject(
        string Key,
        byte[] Body,
        string? ContentType,
        string? ContentEncoding,
        string? CacheControl,
        bool UseChunkEncoding);

    private sealed class Fixture
    {
        public Fixture(bool compress)
        {
            IAmazonS3 s3 = Substitute.For<IAmazonS3>();

            // ONLY PutObjectAsync is stubbed, deliberately. The sink discards the results of
            // CopyObjectAsync and DeleteObjectAsync, and NSubstitute returns a COMPLETED task
            // for an unconfigured member rather than a null one, so awaiting them is
            // harmless. GetLiveChecksumsAsync — the one member that would care — is never
            // called from a stage. Adding more stubs here would be noise pretending to be
            // rigour; the stateful bucket lives in the integration suite.
            s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Capture(call.Arg<PutObjectRequest>()!)));

            Sink = new SitemapSink(
                s3,
                Options.Create(new SeoOptions
                {
                    SiteUrl = Site,
                    R2 = new R2Options
                    {
                        Bucket = "kariyer-seo-test",

                        // Non-empty on purpose. The default is "", which makes the live key
                        // identical to the stored name and would hide any prefix bug.
                        Prefix = "sitemaps/",
                        StagingPrefix = "_staging/",
                        Compress = compress,
                    },
                }),
                NullLogger<SitemapSink>.Instance);
        }

        public SitemapSink Sink { get; }

        /// <summary>Objects PUT to the staging prefix, in order.</summary>
        public List<StoredObject> Puts { get; } = [];

        /// <summary>Writes one chunk file through the real writer, which disposes the stream.</summary>
        public void WriteUrlSet(ISitemapStage stage, string baseName, params string[] slugs)
        {
            IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
                baseName,
                slugs.Select(s => SitemapUrl.At(JobUrl.For(Site, s))),
                fileName => stage.OpenWrite(fileName, "application/xml"));

            foreach (SitemapChunk chunk in chunks)
            {
                stage.RecordChecksum(chunk.FileName, chunk.Checksum);
            }
        }

        public async Task WriteIndexAndRobotsAsync(ISitemapStage stage)
        {
            // Exactly SitemapBuilder's shape — an `await using` block around the stream,
            // because WriteIndex and WriteText do not dispose what they are handed. That
            // dispose is what closes the gzip envelope and writes its trailer, and it is what
            // CommitAsync must not then try to flush.
            await using (Stream destination = stage.OpenWrite(SitemapNames.Index, "application/xml"))
            {
                SitemapChunk index = SitemapWriter.WriteIndex(
                    SitemapNames.Index,
                    [new SitemapIndexEntry($"{Site}/sitemap-jobs-1.xml.gz", null)],
                    destination);

                stage.RecordChecksum(index.FileName, index.Checksum);
            }

            await WriteRobotsAsync(stage);
        }

        public async Task WriteRobotsAsync(ISitemapStage stage)
        {
            await using Stream destination = stage.OpenWrite(SitemapNames.Robots, "text/plain");

            SitemapChunk robots = SitemapWriter.WriteText(
                SitemapNames.Robots,
                RobotsPolicy.Build(Site, "/" + SitemapNames.Index, ["/api/"], allowIndexing: true),
                destination);

            stage.RecordChecksum(robots.FileName, robots.Checksum);
        }

        private PutObjectResponse Capture(PutObjectRequest request)
        {
            // Copied HERE, synchronously, inside the stub. request.InputStream is a FileStream
            // over a temp file that PutAsync disposes the instant this returns and the stage
            // deletes on teardown — a fake that stashed the Stream and read it from the
            // assertion would be reading a closed handle over a deleted file.
            using MemoryStream buffer = new();
            request.InputStream.CopyTo(buffer);

            Puts.Add(new StoredObject(
                request.Key,
                buffer.ToArray(),
                request.ContentType,
                request.Headers.ContentEncoding,
                request.Headers.CacheControl,
                request.UseChunkEncoding));

            return new PutObjectResponse();
        }
    }
}
