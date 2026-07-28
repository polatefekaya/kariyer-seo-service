namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// One <c>&lt;url&gt;</c> entry.
/// </summary>
/// <param name="Loc">
/// The absolute, canonical URL. Built through <see cref="Urls.JobUrl"/> or
/// <see cref="Urls.FacetUrl"/> and never by string concatenation at a call site — a sitemap
/// URL that differs from the SPA's canonical by so much as a trailing slash is a duplicate
/// in Google's eyes, and duplicates are how a page loses to itself.
/// </param>
/// <param name="LastModified">
/// <c>company_job.modified_on</c> for a job, the corpus's newest modification for a facet.
///
/// Optional because omitting the element is a valid and honest answer, whereas emitting
/// "now" for a page that has not changed is not: since Google deprecated the sitemap ping
/// endpoint, <c>&lt;lastmod&gt;</c> IS the freshness signal, and a file where every entry
/// claims to have changed on every rebuild teaches the crawler to ignore the field.
/// </param>
public readonly record struct SitemapUrl(string Loc, DateTimeOffset? LastModified)
{
    public static SitemapUrl At(string loc, DateTimeOffset? lastModified = null) =>
        new(loc, lastModified);
}
