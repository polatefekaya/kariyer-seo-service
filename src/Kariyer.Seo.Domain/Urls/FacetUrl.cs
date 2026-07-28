namespace Kariyer.Seo.Domain.Urls;

/// <summary>
/// The canonical job-filter (facet) URL.
///
/// Facet paths are NOT composed here — they arrive whole from the facet manifest the web
/// app publishes, already built by the same <c>buildJobFilterPath</c> the SPA's chips, hubs
/// and canonical tags use. This type only makes them absolute.
///
/// That division is deliberate and is the whole reason the manifest exists. The city-first
/// hierarchy, the reserved work-type prefix (<c>uzaktan-yazilim-muhendisi</c>), the Turkish
/// slug folding and the curated sector/position registries all live in the web repo. A
/// second implementation of those rules here would be correct on the day it was written and
/// wrong on the first day someone adds a sector — and the symptom would be a sitemap full of
/// URLs that 301 or 404.
/// </summary>
public static class FacetUrl
{
    /// <summary>Every facet path lives under this prefix. Used to reject manifest
    /// entries that are not job-filter pages at all.</summary>
    public const string PathPrefix = "/is-ilanlari";

    public static string For(string siteUrl, string facetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facetPath);
        return SiteUrls.Absolute(siteUrl, facetPath);
    }

    /// <summary>
    /// Whether a manifest entry is a plausible facet path.
    ///
    /// Checked rather than trusted because the manifest is fetched over HTTP from another
    /// repo's build output: a malformed deploy there must not be able to put an absolute
    /// URL, a protocol-relative <c>//evil.example</c>, or a path traversal into our sitemap.
    /// </summary>
    public static bool IsFacetPath(string? path) =>
        path is not null
        && path.Length > 1
        && path[0] == '/'
        && path[1] != '/'
        && !path.Contains("..", StringComparison.Ordinal)
        && (path == PathPrefix || path.StartsWith(PathPrefix + "/", StringComparison.Ordinal));
}
