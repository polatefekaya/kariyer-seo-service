namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// One row per sitemap file produced (PLAN §8). Two jobs, both load-bearing.
///
/// <b>The conditional-write short-circuit.</b> A rebuild compares the checksum it just
/// computed against the last one here and skips the upload when they match. Most cron ticks
/// change nothing, so this turns the steady state from "re-upload the whole set every 45
/// minutes" into "upload nothing" — and, because it also skips the event, it stops
/// <c>SitemapRebuiltEvent</c> from becoming a heartbeat that nobody can distinguish from
/// real news.
///
/// <b>The audit trail.</b> When the question is "when did this URL leave the sitemap, and
/// how many were in it at the time", this table is the answer. There is no other record:
/// R2 holds only the current file, and the previous one is gone the moment it is replaced.
/// </summary>
public sealed class SeoRebuildLog
{
    public long Id { get; set; }

    /// <summary>File name as stored, e.g. <c>sitemap-jobs-1.xml</c>.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Which logical set it belongs to — jobs | jobfilters | static | index.</summary>
    public string SitemapType { get; set; } = string.Empty;

    public int UrlCount { get; set; }

    /// <summary>
    /// Hex SHA-256 of the UNCOMPRESSED XML.
    ///
    /// Uncompressed because gzip output depends on the zlib the runtime links, so a
    /// checksum over compressed bytes would differ after a base-image bump even when not one
    /// URL moved — re-uploading the entire set on every deploy and, worse, making "the
    /// checksum changed" useless as evidence that the corpus changed.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>Size of the XML before compression, for the 50 MiB headroom check.</summary>
    public long UncompressedBytes { get; set; }

    /// <summary>Whether this run actually uploaded, or short-circuited on an equal checksum.
    /// A long run of false is the healthy steady state; a long run of true on a quiet
    /// catalogue means the checksum is not stable and the short-circuit is not working.</summary>
    public bool Uploaded { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
}
