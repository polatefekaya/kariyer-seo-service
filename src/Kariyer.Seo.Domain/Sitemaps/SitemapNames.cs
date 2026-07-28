namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// The file names of the set, in one place.
///
/// They are constants rather than configuration on purpose. <c>/sitemap.xml</c> is submitted
/// to Search Console exactly once and then referenced from <c>robots.txt</c> forever; the
/// Cloudflare rule that maps <c>/sitemap*.xml</c> to the R2 bucket is written against this
/// shape. Making any of it configurable would mean a value could be changed in one
/// environment and silently orphan the file Google has been fetching for a year.
/// </summary>
public static class SitemapNames
{
    /// <summary>The index. The only file whose URL is ever submitted anywhere.</summary>
    public const string Index = "sitemap.xml";

    /// <summary>Base name for job-detail chunks; files are <c>sitemap-jobs-1.xml</c>, … .</summary>
    public const string JobsBase = "sitemap-jobs";

    /// <summary>Base name for the indexable job-filter facets.</summary>
    public const string JobFiltersBase = "sitemap-jobfilters";

    /// <summary>
    /// Base name for CMS landing pages published by <c>kariyer-cms-service</c>.
    ///
    /// A file of their own rather than folded into <c>sitemap-static</c>, even though both
    /// are "not jobs". Static paths are a hand-edited config list that changes on a deploy;
    /// CMS pages are data that changes whenever an editor clicks Publish. Keeping them apart
    /// means an editor's change re-uploads one small file instead of invalidating the file
    /// that carries the home page, and the per-file URL counts on the diagnostics endpoint
    /// stay meaningful.
    /// </summary>
    public const string PagesBase = "sitemap-pages";

    /// <summary>Base name for the hand-listed static pages.</summary>
    public const string StaticBase = "sitemap-static";

    public const string Robots = "robots.txt";

    /// <summary>Values of <c>SitemapRebuiltEvent.SitemapType</c>.</summary>
    public static class Types
    {
        public const string Jobs = "jobs";
        public const string JobFilters = "jobfilters";
        public const string Pages = "pages";
        public const string Static = "static";
        public const string Index = "index";
    }

    /// <summary>
    /// True when a stored file name belongs to this service's set.
    ///
    /// Used before deleting anything from the bucket prefix, so a bucket that also holds
    /// something else can never have that something else garbage-collected by a rebuild.
    /// </summary>
    public static bool IsOwned(string fileName) =>
        fileName == Index
        || fileName == Robots
        || fileName.StartsWith(JobsBase + "-", StringComparison.Ordinal)
        || fileName.StartsWith(JobFiltersBase + "-", StringComparison.Ordinal)
        || fileName.StartsWith(PagesBase + "-", StringComparison.Ordinal)
        || fileName.StartsWith(StaticBase + "-", StringComparison.Ordinal);
}
