namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// The protocol's hard caps (sitemaps.org 0.9), as constants rather than magic numbers.
///
/// Both are enforced, not just the first. A file that exceeds either is rejected wholesale —
/// Google does not partially read an oversized sitemap, it discards it — so a corpus that
/// grew past a limit would silently de-index the entire chunk rather than the surplus.
/// </summary>
public static class SitemapLimits
{
    /// <summary>Maximum <c>&lt;url&gt;</c> entries in one urlset.</summary>
    public const int MaxUrlsPerFile = 50_000;

    /// <summary>Maximum <c>&lt;sitemap&gt;</c> entries in one index.</summary>
    public const int MaxSitemapsPerIndex = 50_000;

    /// <summary>
    /// Maximum UNCOMPRESSED size of one file, in bytes (50 MiB).
    ///
    /// Uncompressed is what the protocol limits, and it is also the only figure this
    /// service can know while streaming: the gzip length is not final until the stream is
    /// closed, by which point a chunk decision would come far too late.
    /// </summary>
    public const long MaxUncompressedBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Bytes reserved for the closing <c>&lt;/urlset&gt;</c> and a safety margin, so a
    /// chunk that fills to <see cref="MaxUncompressedBytes"/> can still be closed validly.
    /// </summary>
    public const long ClosingReserveBytes = 1024;
}
