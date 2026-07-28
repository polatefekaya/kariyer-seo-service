using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// Turns a sequence of <see cref="SitemapUrl"/> into sitemaps.org 0.9 XML, chunked at the
/// protocol limits.
///
/// Three properties this type is responsible for, each of which is a golden test:
///
/// <b>Streaming.</b> URLs arrive as an <see cref="IEnumerable{T}"/> and go straight into the
/// destination stream. Nothing accumulates: a 400k-URL corpus costs the same memory as a
/// 400-URL one. That matters because the alternative — building the document in memory and
/// then compressing it — is what turns a growing catalogue into an OOM on a small pod, and
/// it would fail exactly when the corpus is largest and the sitemap matters most.
///
/// <b>Determinism.</b> The same input produces byte-identical output, every time, on every
/// machine: fixed encoding, fixed indentation, invariant date formatting, no ambient clock.
/// Without that the checksum short-circuit in PLAN §7 is worthless — every cron tick would
/// look like a change and re-upload the whole set.
///
/// <b>Chunking that respects BOTH caps.</b> A file is closed at 50,000 URLs or at 50 MiB,
/// whichever comes first. Only counting URLs would be a silent trap: 50k long Turkish slugs
/// clear the byte limit comfortably, but the day a URL scheme grows they would not, and
/// Google discards an oversized sitemap whole rather than truncating it — the failure would
/// be an entire chunk of the catalogue vanishing from the index, with nothing in this
/// service to say why.
///
/// The destination stream is supplied by the caller (the R2 sink, or a MemoryStream in a
/// test). This type never opens one, which is what keeps it in the pure domain.
/// </summary>
public static class SitemapWriter
{
    private const string UrlSetNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>
    /// Writer settings shared by every document this service emits.
    ///
    /// <c>Indent</c> is on deliberately even though crawlers do not care: these files get
    /// opened by humans during an SEO incident, and a 50 MB single-line document is not
    /// something anyone can diff or eyeball. The cost is a few percent of a gzip that
    /// compresses whitespace to almost nothing.
    ///
    /// UTF-8 WITHOUT a BOM: some crawlers and validators treat a leading BOM as content
    /// before the XML declaration and reject the file.
    /// </summary>
    private static XmlWriterSettings Settings(bool closeOutput) => new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        CloseOutput = closeOutput,
        Async = false,
    };

    /// <summary>
    /// Writes as many chunks as the input needs, asking the caller for a destination stream
    /// per chunk.
    /// </summary>
    /// <param name="baseName">
    /// Chunk base, e.g. <c>sitemap-jobs</c>. Chunks are named <c>{baseName}-{n}.xml</c>
    /// starting at 1 — always numbered, even when there is exactly one, so a corpus crossing
    /// 50k does not silently rename <c>sitemap-jobs.xml</c> to <c>sitemap-jobs-1.xml</c> and
    /// leave the old file orphaned and still indexed.
    /// </param>
    /// <param name="urls">The entries, streamed. Enumerated exactly once.</param>
    /// <param name="openChunk">
    /// Supplies a writable stream for a chunk file name. The returned stream is disposed by
    /// this method; whatever the caller layered underneath (gzip, an S3 multipart upload) is
    /// therefore flushed before the chunk is reported.
    /// </param>
    /// <returns>One entry per chunk written, in order. Never empty — see below.</returns>
    public static IReadOnlyList<SitemapChunk> WriteUrlSets(
        string baseName,
        IEnumerable<SitemapUrl> urls,
        Func<string, Stream> openChunk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(openChunk);

        List<SitemapChunk> chunks = [];
        int chunkNumber = 0;

        using IEnumerator<SitemapUrl> enumerator = urls.GetEnumerator();
        bool hasMore = enumerator.MoveNext();

        // An empty corpus still emits one empty urlset rather than no file at all. Skipping
        // the write would leave the PREVIOUS chunk live on R2 — a sitemap full of URLs that
        // the corpus no longer contains, served indefinitely with nothing to correct it.
        // An empty file is the honest statement, and it is a valid document.
        do
        {
            chunkNumber++;
            string fileName = $"{baseName}-{chunkNumber.ToString(CultureInfo.InvariantCulture)}.xml";

            chunks.Add(WriteOneUrlSet(fileName, enumerator, ref hasMore, openChunk));
        }
        while (hasMore);

        return chunks;
    }

    /// <summary>
    /// Writes the sitemap index — the single file Search Console is given, and the only one
    /// whose URL is ever submitted anywhere.
    /// </summary>
    /// <param name="children">Child sitemaps, in the order they should appear.</param>
    /// <param name="destination">Where to write. Not disposed by this method.</param>
    public static SitemapChunk WriteIndex(
        string fileName,
        IReadOnlyList<SitemapIndexEntry> children,
        Stream destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(destination);

        if (children.Count > SitemapLimits.MaxSitemapsPerIndex)
        {
            // Not clamped. Silently dropping children would de-index whatever fell off the
            // end, and at this scale the correct answer is a nested index — a deliberate
            // design change, not something to paper over at write time.
            throw new ArgumentOutOfRangeException(
                nameof(children),
                children.Count,
                $"A sitemap index holds at most {SitemapLimits.MaxSitemapsPerIndex} children. "
                + "Beyond that the set needs a nested index, which is a deliberate change.");
        }

        CountingStream counting = new(destination);
        DateTimeOffset? newest = null;

        using (XmlWriter writer = XmlWriter.Create(counting, Settings(closeOutput: false)))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("sitemapindex", UrlSetNamespace);

            foreach (SitemapIndexEntry child in children)
            {
                writer.WriteStartElement("sitemap", UrlSetNamespace);
                writer.WriteElementString("loc", UrlSetNamespace, child.Loc);

                if (child.LastModified is { } lastModified)
                {
                    writer.WriteElementString("lastmod", UrlSetNamespace, Format(lastModified));

                    if (newest is null || lastModified > newest)
                    {
                        newest = lastModified;
                    }
                }

                writer.WriteEndElement();
            }

            // WriteFullEndElement, not WriteEndElement: an index with no children would
            // otherwise collapse to a self-closing `<sitemapindex/>`. That is well-formed
            // XML, but several sitemap validators and at least one crawler treat a
            // self-closed root as malformed rather than as empty — and "the file Google
            // silently refuses" is not a failure mode this service can observe.
            writer.WriteFullEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        return new SitemapChunk(
            fileName, children.Count, counting.BytesWritten, counting.Checksum(), newest);
    }

    /// <summary>Writes plain text (robots.txt) through the same checksum/counting path.</summary>
    public static SitemapChunk WriteText(string fileName, string content, Stream destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);

        CountingStream counting = new(destination);

        // No BOM, and the exact bytes of the string — robots.txt is parsed line by line by
        // things far less forgiving than an XML reader.
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        counting.Write(bytes, 0, bytes.Length);
        counting.Flush();

        return new SitemapChunk(fileName, 0, counting.BytesWritten, counting.Checksum(), null);
    }

    private static SitemapChunk WriteOneUrlSet(
        string fileName,
        IEnumerator<SitemapUrl> enumerator,
        ref bool hasMore,
        Func<string, Stream> openChunk)
    {
        int urlCount = 0;
        DateTimeOffset? newest = null;
        CountingStream counting;

        using (Stream destination = openChunk(fileName))
        {
            counting = new CountingStream(destination);

            using XmlWriter writer = XmlWriter.Create(counting, Settings(closeOutput: false));

            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", UrlSetNamespace);

            while (hasMore)
            {
                SitemapUrl url = enumerator.Current;

                writer.WriteStartElement("url", UrlSetNamespace);
                writer.WriteElementString("loc", UrlSetNamespace, url.Loc);

                if (url.LastModified is { } lastModified)
                {
                    writer.WriteElementString("lastmod", UrlSetNamespace, Format(lastModified));

                    if (newest is null || lastModified > newest)
                    {
                        newest = lastModified;
                    }
                }

                writer.WriteEndElement();
                urlCount++;

                hasMore = enumerator.MoveNext();

                // Flush before measuring. XmlWriter buffers, so an unflushed writer reports
                // a byte count that lags reality by up to a buffer — which is exactly the
                // margin a 50 MiB cap cannot afford to be wrong by.
                writer.Flush();

                if (urlCount >= SitemapLimits.MaxUrlsPerFile
                    || counting.BytesWritten >= SitemapLimits.MaxUncompressedBytes
                                                - SitemapLimits.ClosingReserveBytes)
                {
                    break;
                }
            }

            // WriteFullEndElement, so an empty chunk is `<urlset></urlset>` rather than
            // `<urlset/>`. An empty jobs sitemap is a real, valid state — a brand-new
            // environment, or a catalogue that genuinely emptied — and it must not be the
            // one document shape a validator rejects.
            writer.WriteFullEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        return new SitemapChunk(
            fileName, urlCount, counting.BytesWritten, counting.Checksum(), newest);
    }

    /// <summary>
    /// W3C datetime in UTC, to the second.
    ///
    /// Normalised to UTC rather than emitted with the source offset so two rebuilds of an
    /// unchanged corpus from pods in different time zones produce identical bytes. Seconds
    /// rather than the full round-trip precision because sub-second detail is noise to a
    /// crawler and would make the file churn on any storage round-trip that truncates.
    /// </summary>
    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pass-through stream that counts bytes and hashes them on the way past.
    ///
    /// Both are needed while STREAMING and neither can be obtained afterwards: the byte
    /// count decides where to chunk, and re-reading an S3 multipart upload to hash it would
    /// mean downloading back what we just uploaded.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _checksum;

        public long BytesWritten { get; private set; }

        /// <summary>Hex SHA-256 of everything written. Finalised on first call.</summary>
        public string Checksum() => _checksum ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value) => Write([value]);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The inner stream belongs to the caller; only the hash is ours.
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
