namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// What <see cref="SitemapWriter"/> produced for one chunk: enough to build the index entry
/// and the rebuild-log row, without the caller having to re-read the bytes it just wrote.
/// </summary>
/// <param name="FileName">Unzipped logical name, e.g. <c>sitemap-jobs-1.xml</c>.</param>
/// <param name="UrlCount">Entries written into this chunk.</param>
/// <param name="UncompressedBytes">Size of the XML as written, before gzip.</param>
/// <param name="Checksum">
/// Hex SHA-256 of the UNCOMPRESSED XML.
///
/// Over the XML rather than the gzip on purpose: gzip output depends on the zlib build the
/// runtime happens to link, so a checksum of the compressed bytes changes on a base-image
/// bump even when not one URL moved. That would fire the conditional-write short-circuit
/// (PLAN §7) on every deploy and re-upload the entire sitemap set for nothing — and, worse,
/// would make "the checksum changed" useless as a signal that the corpus actually changed.
/// </param>
/// <param name="NewestLastModified">
/// The newest <c>&lt;lastmod&gt;</c> in this chunk, or null if none carried one. This is
/// what the index entry reports, so a crawler re-fetches a child only when something inside
/// it genuinely moved.
/// </param>
public readonly record struct SitemapChunk(
    string FileName,
    int UrlCount,
    long UncompressedBytes,
    string Checksum,
    DateTimeOffset? NewestLastModified);
