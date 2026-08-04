using System.ComponentModel.DataAnnotations;

namespace Kariyer.Seo.Worker.Common.Configuration;

/// <summary>What this service builds and where it puts it (PLAN §10).</summary>
public sealed class SeoOptions
{
    public const string SectionName = "Seo";

    /// <summary>
    /// Site origin, used to make every emitted URL absolute.
    ///
    /// It must match the SPA's canonical host EXACTLY — scheme, host, no trailing path.
    /// <c>http://</c> instead of <c>https://</c>, or a <c>www.</c> the site does not use,
    /// produces a sitemap full of URLs that redirect, which wastes crawl budget and tells
    /// Google the file is stale.
    /// </summary>
    [Required]
    [Url]
    public string SiteUrl { get; init; } = "https://kariyerzamani.com";

    /// <summary>
    /// How often the full-corpus rebuild runs. This is the correctness backstop: whatever
    /// the incremental path gets wrong is corrected within one interval, so the interval IS
    /// the staleness bound PLAN §0 promises.
    /// </summary>
    public TimeSpan CronInterval { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>
    /// How long a burst of freshness events is coalesced before <c>sitemap-jobs</c> is
    /// re-projected. Bounds how long a withdrawn job stays in the sitemap.
    /// </summary>
    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether this deployment's host may be crawled and indexed at all.
    ///
    /// MUST be false on every non-production host. A staging or test deployment is a complete
    /// copy of the site at a different name; left crawlable it competes with production for
    /// the same queries on the same content, and publishing a sitemap for it is the most
    /// effective way to make that happen.
    ///
    /// Defaults to TRUE, which is the uncomfortable but correct choice: the alternative is a
    /// production deployment that forgets to set it and silently de-lists the entire site,
    /// which is far worse and far harder to notice than a test host that gets crawled. The
    /// startup log states which way it resolved, loudly, on every boot.
    /// </summary>
    public bool AllowIndexing { get; init; } = true;

    public ThresholdOptions Thresholds { get; init; } = new();

    public FacetManifestOptions FacetManifest { get; init; } = new();

    public R2Options R2 { get; init; } = new();

    /// <summary>
    /// Hand-listed evergreen pages for <c>sitemap-static.xml</c>. These carry no
    /// <c>&lt;lastmod&gt;</c>: this service has no idea when a marketing page last changed,
    /// and inventing a timestamp would be a lie a crawler acts on.
    ///
    /// The values live in <c>appsettings.json</c>, not here. See the note on
    /// <see cref="DisallowedPaths"/> — an initialiser with entries in it doubles them.
    /// </summary>
    public IReadOnlyList<string> StaticPaths { get; init; } = [];

    /// <summary>
    /// Paths written into <c>robots.txt</c> as <c>Disallow</c>.
    ///
    /// Kept short deliberately. robots.txt controls CRAWLING, not indexing — a disallowed
    /// URL can still be indexed from an external link, and because the crawler may not fetch
    /// it, it can never see the <c>noindex</c> that would remove it. Thin facets are handled
    /// by the count gate, never by a rule here.
    ///
    /// <b>Empty here on purpose. Do not put the defaults back.</b> The configuration binder
    /// APPENDS to a collection property rather than replacing it, so an initialiser with
    /// entries in it survives alongside the bound values and every entry present in both is
    /// emitted twice. That shipped: <c>sitemap-static-1.xml</c> carried 14 URLs of which 7
    /// were unique, and every <c>Disallow</c> line in robots.txt appeared twice — duplicate
    /// <c>&lt;loc&gt;</c> being invalid per the sitemaps protocol. Nothing detects it at
    /// runtime, which is why <c>SeoOptionsExtensions</c> now refuses to start on a duplicate.
    ///
    /// <c>appsettings.json</c> is the single source, and it ships inside the image, so this
    /// costs no resilience — there is no deployment in which the file is absent but the
    /// binary is present. The entries and the reasoning for each live there.
    /// </summary>
    public IReadOnlyList<string> DisallowedPaths { get; init; } = [];

    /// <summary>
    /// Cache-Control written onto every R2 object.
    ///
    /// Shorter than the cron interval on purpose: Cloudflare sits in front of these files,
    /// and a TTL longer than the rebuild period would let a purged job keep being served
    /// from an edge cache after the sitemap behind it had already dropped it.
    /// </summary>
    [Required]
    public string CacheControl { get; init; } = "public, max-age=600";
}

/// <summary>The two indexation thresholds (PLAN §10). They mirror
/// <c>src/seo/facets/indexation.ts</c> in the web app and must be changed together.</summary>
public sealed class ThresholdOptions
{
    [Range(1, 100_000)]
    public int SingleAxis { get; init; } = Domain.Indexation.IndexationPolicy.DefaultSingleAxisMinimum;

    [Range(1, 100_000)]
    public int Combo { get; init; } = Domain.Indexation.IndexationPolicy.DefaultComboMinimum;
}

/// <summary>Where the candidate facet list is fetched from.</summary>
public sealed class FacetManifestOptions
{
    [Required]
    [Url]
    public string Url { get; init; } = "https://kariyerzamani.com/seo/facet-manifest.json";

    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How long a successfully fetched manifest is reused.
    ///
    /// The manifest changes only when the web app deploys, so re-fetching it on every cron
    /// tick is pure waste — and worse, it makes a rebuild depend on the web app being up at
    /// that exact minute. Caching turns "the web app is deploying" from a failed rebuild
    /// into a rebuild against the last known-good candidate set.
    /// </summary>
    public TimeSpan CacheFor { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>
/// The R2 (S3-compatible) bucket the sitemaps are served from.
///
/// Credentials come from the environment in every deployed environment; the blank defaults
/// exist so a developer can boot the service without them and have startup validation
/// explain what is missing, rather than have the first rebuild fail 45 minutes later.
/// </summary>
public sealed class R2Options
{
    /// <summary>Account endpoint, e.g. <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>.</summary>
    public string Endpoint { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    /// <summary>Key prefix inside the bucket. Empty means the bucket root.</summary>
    public string Prefix { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Prefix under which a rebuild stages files before promoting them (PLAN §6.3).
    ///
    /// It must be a path a crawler cannot reach. The Cloudflare rule maps
    /// <c>/sitemap*.xml</c> to the bucket, and this prefix deliberately does not match that
    /// pattern — a staging key that did would serve half-written files to Googlebot, which
    /// is the exact failure the staging step exists to prevent.
    /// </summary>
    [Required]
    public string StagingPrefix { get; init; } = "_staging/";

    /// <summary>
    /// Gzip the uploaded XML. Google accepts <c>.xml.gz</c> sitemaps and the corpus
    /// compresses roughly tenfold, so this is worth having when the bucket is served
    /// directly.
    ///
    /// <b>Defaults to false because Cloudflare fronts this bucket.</b> Pre-compressing in
    /// object storage is only right when object storage is the origin a client talks to.
    /// Behind a CDN it duplicates a job the edge does better — the edge negotiates brotli or
    /// gzip per client, from the same stored object — and it creates an encoding hazard the
    /// edge cannot see through. Observed in production with this on: a client sending
    /// <c>Accept-Encoding: gzip</c> needed TWO gunzip passes to reach the XML, because
    /// Cloudflare re-compressed a body that was already gzip; a client sending none received
    /// gzip bytes with no <c>Content-Encoding</c> at all and <c>Content-Type:
    /// application/xml</c>, which is simply binary mislabelled as a document. Fetched
    /// straight from R2 both were fine, so the stored objects were never the problem —
    /// storing them compressed at all was.
    ///
    /// <b>Turning this on changes the URLs.</b> The stored keys gain a <c>.gz</c> suffix and
    /// the sitemap index names its children with it, so the Cloudflare route that maps
    /// <c>/sitemap-*.xml</c> to the bucket has to map to <c>.xml.gz</c> in the same change.
    /// Flip one without the other and the index becomes a list of 404s — see
    /// <c>docs/DEPLOYMENT.md</c> §4.
    ///
    /// Set it to true only for a deployment serving straight from the bucket with no CDN.
    /// </summary>
    public bool Compress { get; init; }

    /// <summary>True once the bucket is addressable at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Bucket)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}
