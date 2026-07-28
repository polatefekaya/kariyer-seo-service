using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Ports;

/// <summary>
/// Read access to the live job corpus. Implemented in the Worker against
/// <c>company_job</c>.
///
/// Read-only is not a convention here, it is the contract: this interface has no write
/// method and never will. <c>company_job</c> belongs to the Node application today and to
/// the job service tomorrow, and the freshness service owns the one legitimate write to it.
/// A sitemap builder that could touch that table would be a second author of the same rows
/// with none of the safety machinery freshness has.
/// </summary>
public interface IJobCorpusReader
{
    /// <summary>
    /// Streams every live job that should appear in <c>sitemap-jobs</c>, ordered
    /// deterministically.
    ///
    /// Streamed rather than returned as a list because the whole point of
    /// <see cref="Sitemaps.SitemapWriter"/> is that a 400k-URL corpus never lands in memory;
    /// materialising it here would move the OOM one layer down rather than removing it.
    ///
    /// The order must be stable across rebuilds. An unordered read would shuffle URLs
    /// between chunk files on every run, so every chunk's checksum would change, the
    /// conditional-write short-circuit would never fire, and the whole set would be
    /// re-uploaded every 45 minutes for no reason.
    /// </summary>
    IAsyncEnumerable<LiveJob> StreamLiveJobsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The single-pass facet aggregate (PLAN §7): one row per distinct combination of the
    /// facetable columns, with a count.
    ///
    /// One query for all ~3,000 candidate facets. See <see cref="LiveJobFacetTuple"/> for
    /// why grouping by the whole tuple — rather than one aggregate per axis — is what makes
    /// combo facets answerable at all.
    /// </summary>
    Task<IReadOnlyList<LiveJobFacetTuple>> GetFacetTuplesAsync(CancellationToken cancellationToken);

    /// <summary>Live jobs in total, for the diagnostics endpoint and the rebuild log.</summary>
    Task<int> CountLiveJobsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One live job, reduced to exactly what a sitemap entry needs.
/// </summary>
/// <param name="Uid">company_job.uid — the key of the local URL-state row.</param>
/// <param name="Slug">company_job.slug_url, used verbatim to build the canonical URL.</param>
/// <param name="LastModified">
/// company_job.modified_on, emitted as <c>&lt;lastmod&gt;</c>. Null when the row has none,
/// in which case the element is omitted rather than filled with the current time — see
/// <see cref="Sitemaps.SitemapUrl.LastModified"/>.
/// </param>
public readonly record struct LiveJob(string Uid, string Slug, DateTimeOffset? LastModified);
