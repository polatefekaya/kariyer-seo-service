namespace Kariyer.Seo.Domain.Urls;

/// <summary>
/// The canonical job-detail URL: <c>{site}/is-ilanlari/ilan/{slug_url}</c>.
///
/// This must match the SPA's canonical byte for byte. Two older shapes —
/// <c>/ilanlar/slug/{slug}</c> and <c>/jobs/slug/{slug}</c> — still resolve via 301 and are
/// still cached by the prerenderer, which is why <see cref="PrerenderKeys"/> purges all
/// three even though only this one is ever emitted into a sitemap. A sitemap must advertise
/// only the destination of a redirect, never a source: listing a 301 wastes crawl budget and
/// tells Google the file is stale.
/// </summary>
public static class JobUrl
{
    /// <summary>Path segment prefix, shared with <see cref="PrerenderKeys"/>.</summary>
    public const string PathPrefix = "/is-ilanlari/ilan/";

    /// <summary>Legacy shapes that still 301 here and still hold prerender snapshots.</summary>
    public const string LegacyIlanlarPrefix = "/ilanlar/slug/";
    public const string LegacyJobsPrefix = "/jobs/slug/";

    /// <summary>
    /// Builds the canonical URL for a slug.
    ///
    /// The slug is used verbatim. It is not re-slugified, re-encoded or lower-cased here:
    /// <c>company_job.slug_url</c> is what the SPA routes on, so any transformation applied
    /// on this side would produce a URL that is correct-looking and 404s.
    /// </summary>
    public static string For(string siteUrl, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return SiteUrls.Absolute(siteUrl, PathPrefix + slug);
    }

    /// <summary>True when a slug can address a page at all. Empty slugs exist in the
    /// corpus and must be skipped rather than emitted as <c>/is-ilanlari/ilan/</c>.</summary>
    public static bool IsAddressable(string? slug) => !string.IsNullOrWhiteSpace(slug);
}
