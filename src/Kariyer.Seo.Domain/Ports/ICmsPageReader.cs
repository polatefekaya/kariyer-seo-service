namespace Kariyer.Seo.Domain.Ports;

/// <summary>
/// Read access to the CMS landing pages published by <c>kariyer-cms-service</c>.
///
/// A separate port from <see cref="IJobCorpusReader"/> because it is a separate bounded
/// context that happens to share a database — <c>cms.seo_page</c> is owned and migrated by
/// another service, and this one is a guest there exactly as it is in <c>public.company_job</c>.
///
/// Read-only, with no write method and no intention of ever having one. The CMS owns
/// publication; this service owns telling a crawler about it.
/// </summary>
public interface ICmsPageReader
{
    /// <summary>
    /// Streams every CMS page that belongs in <c>sitemap-pages.xml</c>, ordered
    /// deterministically.
    ///
    /// "Belongs" means indexable, not merely reachable — see <see cref="CmsPage"/>. The order
    /// must be stable across rebuilds for the same reason the job corpus's is: an unordered
    /// read changes the file's checksum on every run, which defeats the conditional-write
    /// short-circuit and re-uploads a file nothing changed in.
    /// </summary>
    IAsyncEnumerable<CmsPage> StreamIndexablePagesAsync(CancellationToken cancellationToken);

    /// <summary>Indexable CMS pages in total, for the diagnostics endpoint.</summary>
    Task<int> CountIndexablePagesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One CMS page, reduced to what a sitemap entry needs.
/// </summary>
/// <param name="Path">
/// <c>cms.seo_page.path</c> — site-relative and already canonical. Used verbatim: the CMS
/// validates and normalises paths on publish, so re-shaping one here could only produce a URL
/// its own resolver would not serve.
/// </param>
/// <param name="LastModified">
/// <c>cms.seo_page.published_at</c>, emitted as <c>&lt;lastmod&gt;</c>.
///
/// Published-at rather than updated-at, deliberately. <c>updated_at</c> moves every time an
/// editor saves a draft, and a draft save changes nothing a crawler can see — advertising it
/// as a modification would ask Google to re-fetch a page that is byte-identical to what it
/// already has.
/// </param>
public readonly record struct CmsPage(string Path, DateTimeOffset? LastModified);
