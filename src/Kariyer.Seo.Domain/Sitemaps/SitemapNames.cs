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
    /// Whether a given file is stored gzip-compressed, when compression is switched on.
    ///
    /// <b>The XML documents only.</b> <c>robots.txt</c> is never compressed however this is
    /// configured, because it is the one file whose URL is fixed: every crawler on the
    /// internet fetches <c>/robots.txt</c> exactly, so it can never carry a <c>.gz</c>
    /// suffix — and a file compressed but not renamed is the worst of both. That is what
    /// shipped: <c>robots.txt</c> was gzipped, stored under a suffix-less key, and served
    /// with <c>Content-Encoding: gzip</c>, so anything fetching it without announcing gzip
    /// support received binary.
    ///
    /// This predicate and <see cref="StoredName"/> are the single source of that rule. They
    /// were previously two separate expressions — one deciding what to compress, one deciding
    /// what to rename — which is exactly how the two came to disagree.
    /// </summary>
    public static bool IsCompressed(string fileName, bool compress) =>
        compress && fileName.EndsWith(".xml", StringComparison.Ordinal);

    /// <summary>
    /// The key a file is stored under, and therefore the URL a crawler fetches it at.
    ///
    /// The sitemap index must name children by THIS, not by the logical file name: an index
    /// naming <c>sitemap-jobs-1.xml</c> for an object stored at <c>sitemap-jobs-1.xml.gz</c>
    /// is an index of 404s, and nothing in this service would report it.
    /// </summary>
    public static string StoredName(string fileName, bool compress) =>
        IsCompressed(fileName, compress) ? fileName + ".gz" : fileName;

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
