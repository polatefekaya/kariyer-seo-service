namespace Kariyer.Seo.Domain.Urls;

/// <summary>
/// The ONE place a site-absolute URL is composed.
///
/// Nothing in this service concatenates a host and a path by hand. The reason is narrow and
/// unforgiving: a sitemap entry that differs from the SPA's canonical tag — a doubled slash,
/// a missing one, a trailing slash the router does not emit — is not "nearly right", it is a
/// second URL. Google then has two addresses for one page, splits their signals, and picks
/// the winner itself. That failure is invisible in every log this service writes.
///
/// So the host is normalised once here, and every builder goes through <see cref="Absolute"/>.
/// </summary>
public static class SiteUrls
{
    /// <summary>
    /// Joins a site origin and a site-relative path into exactly one absolute URL.
    /// </summary>
    /// <param name="siteUrl">Origin, e.g. <c>https://kariyerzamani.com</c>. A trailing
    /// slash is tolerated and stripped rather than rejected, because it is the single most
    /// common way this value arrives from an environment variable.</param>
    /// <param name="path">Site-relative path beginning with <c>/</c>.</param>
    public static string Absolute(string siteUrl, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentNullException.ThrowIfNull(path);

        string origin = siteUrl.TrimEnd('/');

        // "" and "/" both mean the home page. Emitting the origin with no trailing slash
        // for the root would produce `https://kariyerzamani.com`, which the SPA canonicals
        // as `https://kariyerzamani.com/` — a duplicate of the most important page on the
        // site.
        if (path.Length == 0 || path == "/")
        {
            return origin + "/";
        }

        return path[0] == '/' ? origin + path : origin + "/" + path;
    }

    /// <summary>Origin with any trailing slash removed, for keys and comparisons.</summary>
    public static string Origin(string siteUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        return siteUrl.TrimEnd('/');
    }
}
