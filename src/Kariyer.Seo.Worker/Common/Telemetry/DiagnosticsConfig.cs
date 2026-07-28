using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kariyer.Seo.Worker.Common.Telemetry;

/// <summary>
/// The service's instrument surface.
///
/// Counters are named WITHOUT a <c>_total</c> suffix because the Prometheus exporter appends
/// it; the scrape endpoint therefore publishes exactly the names the runbook and alerts refer
/// to (<c>seo_sitemap_rebuilds_total</c>, …).
///
/// The framing that matters here: this service has NO user-facing failure mode. Nothing 500s
/// when the sitemap is wrong, no request is slow, no customer complains within the hour. A
/// rebuild that has been failing for a week and a rebuild that had nothing to do look
/// identical from outside. So the instruments below are chosen to make silence loud —
/// <see cref="RebuildFailures"/> and the absence of <see cref="Rebuilds"/> are the two things
/// worth paging on.
/// </summary>
public static class DiagnosticsConfig
{
    public const string ServiceName = "seo-service";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // ── Rebuilds ────────────────────────────────────────────────────────────────
    public static readonly Counter<long> Rebuilds = Meter.CreateCounter<long>(
        "seo_sitemap_rebuilds",
        description: "Full-corpus rebuilds completed, tagged by trigger (cron | manual). "
                   + "ALERT ON ABSENCE: a builder that has stopped still passes every health "
                   + "check, and the only other symptom is a sitemap that quietly stops changing.");

    public static readonly Counter<long> RebuildFailures = Meter.CreateCounter<long>(
        "seo_sitemap_rebuild_failures",
        description: "Rebuild attempts that threw. The staged set is discarded and the live one "
                   + "is untouched, so this is never data loss — but a sustained rate means the "
                   + "sitemap is frozen at whatever it last was.");

    public static readonly Histogram<double> RebuildDuration = Meter.CreateHistogram<double>(
        "seo_sitemap_rebuild_seconds",
        unit: "s",
        description: "Wall-clock time of a full rebuild. Growth tracks catalogue growth; a step "
                   + "change usually means the streaming read started buffering.");

    public static readonly Gauge<long> SitemapUrlCount = Meter.CreateGauge<long>(
        "seo_sitemap_urls",
        description: "URLs in the last built set, tagged by sitemap_type. A sudden drop is the "
                   + "single most damaging thing this service can do — alert on it.");

    // ── R2 ──────────────────────────────────────────────────────────────────────
    public static readonly Counter<long> SitemapUploads = Meter.CreateCounter<long>(
        "seo_sitemap_uploads",
        description: "Objects written to R2, tagged by file. On a quiet catalogue this should be "
                   + "near ZERO between real changes — a steady stream means the checksum "
                   + "short-circuit is not working and every tick is re-uploading the whole set.");

    public static readonly Counter<long> SitemapSwaps = Meter.CreateCounter<long>(
        "seo_sitemap_swaps",
        description: "Staged sets promoted to live. One per rebuild that changed something.");

    public static readonly Counter<long> ChecksumSkips = Meter.CreateCounter<long>(
        "seo_sitemap_checksum_skips",
        description: "Uploads skipped because the file was byte-identical to the live one. The "
                   + "healthy steady state; this should dominate seo_sitemap_uploads_total.");

    // ── Freshness reactions ─────────────────────────────────────────────────────
    public static readonly Counter<long> JobsRemoved = Meter.CreateCounter<long>(
        "seo_jobs_removed",
        description: "Job URLs dropped from the sitemap in response to JobExpiredEvent.");

    public static readonly Counter<long> JobsReadmitted = Meter.CreateCounter<long>(
        "seo_jobs_readmitted",
        description: "Job URLs re-admitted in response to JobResurrectedEvent. Non-zero means the "
                   + "freshness service produced false expiries upstream.");

    public static readonly Counter<long> PrerenderPurges = Meter.CreateCounter<long>(
        "seo_prerender_purges",
        description: "Garnet keys deleted, tagged by outcome. ZERO over a period with non-zero "
                   + "seo_jobs_removed_total is the bad case: jobs are leaving the sitemap while "
                   + "the prerenderer keeps serving their rendered 'apply now' page until TTL.");

    public static readonly Counter<long> PrerenderPurgeFailures = Meter.CreateCounter<long>(
        "seo_prerender_purge_failures",
        description: "Purges that could not reach Garnet. Retried on the next flush or "
                   + "redelivery; the cache TTL is the backstop.");

    public static readonly Counter<long> DirtyFlushes = Meter.CreateCounter<long>(
        "seo_dirty_flushes",
        description: "Debounced incremental re-projections of sitemap-jobs. Should be far FEWER "
                   + "than seo_jobs_removed_total — if they are equal, coalescing is not "
                   + "happening and each expiry is re-projecting the whole file on its own.");

    // ── CMS pages ───────────────────────────────────────────────────────────────
    public static readonly Counter<long> CmsPagesChanged = Meter.CreateCounter<long>(
        "seo_cms_pages_changed",
        description: "CMS page publish/unpublish events reacted to, tagged by action. Compare "
                   + "against the CMS service's own publish count: a persistent gap means this "
                   + "service is not receiving the events and CMS pages only reach the sitemap "
                   + "on the 45-minute cron.");

    // ── Facets ──────────────────────────────────────────────────────────────────
    public static readonly Gauge<long> IndexableFacets = Meter.CreateGauge<long>(
        "seo_indexable_facets",
        description: "Facets clearing their live-job threshold at the last rebuild.");

    public static readonly Counter<long> FacetTransitions = Meter.CreateCounter<long>(
        "seo_facet_transitions",
        description: "Facets crossing the indexability threshold, tagged by direction.");

    public static readonly Counter<long> FacetManifestFetches = Meter.CreateCounter<long>(
        "seo_facet_manifest_fetches",
        description: "Manifest fetches, tagged by outcome (fresh | cached | failed). Sustained "
                   + "'failed' means the filter sitemap is being built from an ageing candidate "
                   + "set — stale, but deliberately never empty.");

    // ── Indexing API (optional) ─────────────────────────────────────────────────
    public static readonly Counter<long> IndexingSubmissions = Meter.CreateCounter<long>(
        "seo_indexing_submissions",
        description: "Google Indexing API calls, tagged by action and outcome.");

    // ── Scheduling ──────────────────────────────────────────────────────────────
    public static readonly Counter<long> WorkerTickFailures = Meter.CreateCounter<long>(
        "seo_worker_tick_failures",
        description: "Recurring-worker ticks that threw, tagged by worker. The loop survives them, "
                   + "which is exactly why this must be alerted on: a builder failing every tick "
                   + "looks perfectly healthy while the sitemap ages indefinitely.");
}
