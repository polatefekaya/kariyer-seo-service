namespace Kariyer.Seo.Domain.Urls;

/// <summary>
/// The Garnet keys holding a job's prerendered HTML.
///
/// The prerenderer keys on the full absolute URL it was asked to render
/// (<c>prerender:${url}</c> in <c>kz-prerender/cache.js</c>), so purging is a matter of
/// reconstructing every URL a crawler could have arrived on.
///
/// All THREE shapes are purged, not just the canonical one. The two legacy paths still 301
/// to the canonical, and a bot that followed one of them got its snapshot cached under the
/// legacy key — so purging only the canonical leaves a withdrawn job serving a fully
/// rendered "apply now" page, from cache, for the whole TTL. That is the exact user-visible
/// failure this service exists to prevent, and it costs two extra DELs to make impossible.
///
/// Purging is idempotent by nature (<c>DEL</c> on a missing key is a no-op), which is what
/// lets the consumer retry freely after a crash between the database commit and the purge.
/// </summary>
public static class PrerenderKeys
{
    public const string Prefix = "prerender:";

    /// <summary>Every key that could hold a snapshot of this job, canonical first.</summary>
    public static string[] For(string siteUrl, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        string origin = SiteUrls.Origin(siteUrl);

        return
        [
            Prefix + origin + JobUrl.PathPrefix + slug,
            Prefix + origin + JobUrl.LegacyIlanlarPrefix + slug,
            Prefix + origin + JobUrl.LegacyJobsPrefix + slug,
        ];
    }

    /// <summary>
    /// The single key holding a snapshot of an arbitrary site path — a CMS landing page.
    ///
    /// One key, not three: the legacy variants exist only for job detail URLs, which were
    /// reachable under two older shapes. A CMS page has never had another address, so
    /// inventing extra keys here would delete nothing and hide a typo in the real one.
    /// </summary>
    public static string ForPath(string siteUrl, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Prefix + SiteUrls.Absolute(siteUrl, path);
    }
}
