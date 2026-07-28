using System.IO.Compression;
using System.Text;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;

namespace Kariyer.Seo.Domain.Tests.Sitemaps;

/// <summary>
/// Golden and property tests for the XML a crawler actually receives.
///
/// Nothing downstream of this service validates its output. Google does not report a
/// malformed sitemap in a way anyone here would see, and a wrong <c>&lt;lastmod&gt;</c> or a
/// chunk boundary in the wrong place is invisible until traffic moves weeks later. These
/// tests are the only place the bytes are checked.
/// </summary>
public sealed class SitemapWriterTests
{
    private const string Site = "https://kariyerzamani.com";

    [Fact]
    public void UrlSetMatchesTheGoldenDocument()
    {
        MemoryStream destination = new();

        SitemapUrl[] urls =
        [
            SitemapUrl.At(JobUrl.For(Site, "yazilim-muhendisi-istanbul-1"),
                new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero)),

            // No lastmod: the element must be OMITTED, not emitted empty and not filled with
            // a made-up timestamp.
            SitemapUrl.At(JobUrl.For(Site, "grafik-tasarimci-ankara-2")),

            // Turkish characters must survive as UTF-8 rather than being escaped or mangled.
            SitemapUrl.At(JobUrl.For(Site, "cagri-merkezi-musteri-temsilcisi-izmir-3"),
                new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.FromHours(3))),
        ];

        IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
            "sitemap-jobs", urls, _ => new NonClosingStream(destination));

        Assert.Single(chunks);
        Assert.Equal("sitemap-jobs-1.xml", chunks[0].FileName);
        Assert.Equal(3, chunks[0].UrlCount);

        Fixtures.AssertMatches("sitemap-jobs-golden.xml", Fixtures.Utf8(destination.ToArray()));
    }

    [Fact]
    public void IndexMatchesTheGoldenDocument()
    {
        MemoryStream destination = new();

        SitemapIndexEntry[] children =
        [
            new($"{Site}/sitemap-jobs-1.xml.gz",
                new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero)),
            new($"{Site}/sitemap-jobfilters-1.xml.gz", null),
            new($"{Site}/sitemap-static-1.xml.gz", null),
        ];

        SitemapWriter.WriteIndex("sitemap.xml", children, destination);

        Fixtures.AssertMatches("sitemap-index-golden.xml", Fixtures.Utf8(destination.ToArray()));
    }

    [Fact]
    public void LastModIsNormalisedToUtcSeconds()
    {
        // Two representations of the SAME instant, in different offsets. They must produce
        // identical bytes — otherwise a pod in a different time zone would rewrite every
        // file it touched, changing every checksum and re-uploading the whole set forever.
        DateTimeOffset utc = new(2026, 6, 15, 19, 0, 0, TimeSpan.Zero);
        DateTimeOffset istanbul = new(2026, 6, 15, 22, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(SitemapWriter.Format(utc), SitemapWriter.Format(istanbul));
        Assert.Equal("2026-06-15T19:00:00Z", SitemapWriter.Format(utc));
    }

    [Fact]
    public void SubSecondPrecisionIsTruncated()
    {
        // Truncated rather than rounded or preserved: sub-second detail is noise to a crawler,
        // and keeping it would make the file churn on any storage round-trip that drops it.
        DateTimeOffset precise = new(2026, 6, 15, 19, 0, 0, 750, TimeSpan.Zero);

        Assert.Equal("2026-06-15T19:00:00Z", SitemapWriter.Format(precise));
    }

    [Fact]
    public void ChunksAtFiftyThousandUrls()
    {
        Dictionary<string, MemoryStream> files = [];

        IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
            "sitemap-jobs",
            Enumerable.Range(0, 100_001).Select(i => SitemapUrl.At($"{Site}/is-ilanlari/ilan/job-{i}")),
            fileName =>
            {
                MemoryStream stream = new();
                files[fileName] = stream;
                return new NonClosingStream(stream);
            });

        // 100,001 URLs is 50,000 + 50,000 + 1. The third chunk existing at all is the point:
        // an off-by-one that put 50,001 into a file would produce a document Google discards
        // WHOLE — losing the entire chunk from the index, not just the surplus URL.
        Assert.Equal(3, chunks.Count);
        Assert.Equal(SitemapLimits.MaxUrlsPerFile, chunks[0].UrlCount);
        Assert.Equal(SitemapLimits.MaxUrlsPerFile, chunks[1].UrlCount);
        Assert.Equal(1, chunks[2].UrlCount);

        Assert.Equal(
            ["sitemap-jobs-1.xml", "sitemap-jobs-2.xml", "sitemap-jobs-3.xml"],
            chunks.Select(c => c.FileName));
    }

    [Fact]
    public void ChunkNumberingStartsAtOneEvenForASingleChunk()
    {
        IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
            "sitemap-jobs",
            [SitemapUrl.At($"{Site}/is-ilanlari/ilan/only")],
            _ => new NonClosingStream(new MemoryStream()));

        // Never `sitemap-jobs.xml`. If a single chunk were unnumbered, the day the corpus
        // crossed 50k the file would be renamed and the old one left orphaned on R2 — still
        // fetchable, still full of URLs, and no longer referenced by anything that would
        // update it.
        Assert.Equal("sitemap-jobs-1.xml", Assert.Single(chunks).FileName);
    }

    [Fact]
    public void AnEmptyCorpusStillProducesOneEmptyFile()
    {
        MemoryStream destination = new();

        IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
            "sitemap-jobs", [], _ => new NonClosingStream(destination));

        // Writing nothing would leave the PREVIOUS chunk live on R2 — a sitemap full of URLs
        // the corpus no longer contains, served indefinitely with nothing to correct it.
        SitemapChunk chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.UrlCount);

        string xml = Fixtures.Utf8(destination.ToArray());
        Assert.Contains("<urlset", xml, StringComparison.Ordinal);
        Assert.Contains("</urlset>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<url>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        // The checksum short-circuit (PLAN §7) is worthless without this: if the same corpus
        // produced different bytes twice, every cron tick would look like a change and
        // re-upload the entire set.
        static byte[] Write()
        {
            MemoryStream stream = new();

            SitemapWriter.WriteUrlSets(
                "sitemap-jobs",
                Enumerable.Range(0, 500).Select(i =>
                    SitemapUrl.At($"{Site}/is-ilanlari/ilan/job-{i}",
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i))),
                _ => new NonClosingStream(stream));

            return stream.ToArray();
        }

        Assert.Equal(Write(), Write());
    }

    [Fact]
    public void ChecksumIsOverTheUncompressedXml()
    {
        MemoryStream plain = new();
        MemoryStream compressed = new();

        SitemapUrl[] urls = [SitemapUrl.At($"{Site}/is-ilanlari/ilan/job-1")];

        SitemapChunk uncompressedChunk = SitemapWriter.WriteUrlSets(
            "sitemap-jobs", urls, _ => new NonClosingStream(plain))[0];

        SitemapChunk gzippedChunk;

        using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzippedChunk = SitemapWriter.WriteUrlSets(
                "sitemap-jobs", urls, _ => new NonClosingStream(gzip))[0];
        }

        // The same checksum whether or not the bytes were compressed on the way out. That is
        // what makes it survive a base-image bump: gzip output depends on the zlib the
        // runtime links, so a checksum over compressed bytes would change on a deploy that
        // moved not one URL, re-uploading the whole set and destroying the signal's meaning.
        Assert.Equal(uncompressedChunk.Checksum, gzippedChunk.Checksum);
        Assert.Equal(64, uncompressedChunk.Checksum.Length);
    }

    [Fact]
    public void GzipRoundTripsToTheExactXml()
    {
        // Deliberately NOT a golden test on the compressed bytes. gzip output is not stable
        // across runtimes or zlib builds, so pinning it would fail on an unrelated upgrade
        // and teach everyone to regenerate the golden without reading it. What must hold is
        // that decompressing yields the exact document — which is what a crawler sees.
        MemoryStream stored = new();
        MemoryStream plain = new();

        SitemapUrl[] urls =
        [
            SitemapUrl.At($"{Site}/is-ilanlari/ilan/muhendis-1",
                new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero)),
        ];

        using (GZipStream gzip = new(stored, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            SitemapWriter.WriteUrlSets("sitemap-jobs", urls, _ => new NonClosingStream(gzip));
        }

        SitemapWriter.WriteUrlSets("sitemap-jobs", urls, _ => new NonClosingStream(plain));

        stored.Position = 0;
        using GZipStream decompress = new(stored, CompressionMode.Decompress);
        MemoryStream restored = new();
        decompress.CopyTo(restored);

        Assert.Equal(
            Encoding.UTF8.GetString(plain.ToArray()),
            Encoding.UTF8.GetString(restored.ToArray()));
    }

    [Fact]
    public void NewestLastModIsCarriedOntoTheChunk()
    {
        // The index reports this per child, and it is the only signal telling a crawler
        // whether re-fetching a 50 MB file is worth it.
        IReadOnlyList<SitemapChunk> chunks = SitemapWriter.WriteUrlSets(
            "sitemap-jobs",
            [
                SitemapUrl.At($"{Site}/a", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                SitemapUrl.At($"{Site}/b", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
                SitemapUrl.At($"{Site}/c", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            ],
            _ => new NonClosingStream(new MemoryStream()));

        Assert.Equal(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            Assert.Single(chunks).NewestLastModified);
    }

    [Fact]
    public void AnOversizedIndexIsRefusedRatherThanTruncated()
    {
        SitemapIndexEntry[] children =
        [
            .. Enumerable.Range(0, SitemapLimits.MaxSitemapsPerIndex + 1)
                .Select(i => new SitemapIndexEntry($"{Site}/sitemap-{i}.xml", null)),
        ];

        // Clamping would silently de-index whatever fell off the end. At this scale the
        // correct answer is a nested index — a deliberate design change, not something to
        // paper over at write time.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SitemapWriter.WriteIndex("sitemap.xml", children, new MemoryStream()));
    }

    /// <summary>
    /// Keeps the underlying MemoryStream readable after the writer disposes its chunk stream.
    /// The production sink genuinely does own and close its streams; a test needs the bytes
    /// afterwards.
    /// </summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Deliberately does not dispose `inner`.
            base.Dispose(disposing);
        }
    }
}
