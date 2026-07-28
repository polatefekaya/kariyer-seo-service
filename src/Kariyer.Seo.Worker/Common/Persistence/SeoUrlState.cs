namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// This service's incremental projection of one job URL (PLAN §8).
///
/// It is a CACHE, not a ledger. Every row here is derivable from <c>company_job</c> by the
/// full rebuild, which is what makes PLAN §0's promise real: the worst this table can do is
/// be stale, and staleness is corrected within one cron interval. Nothing downstream is ever
/// permitted to treat it as the truth about whether a job exists.
///
/// What it buys is latency. Between rebuilds a freshness event can flip one job and have
/// <c>sitemap-jobs</c> re-projected from this table in seconds, without re-reading a
/// several-hundred-thousand-row corpus.
/// </summary>
public sealed class SeoUrlState
{
    /// <summary>company_job.uid.</summary>
    public string JobUid { get; set; } = string.Empty;

    /// <summary>
    /// company_job.slug_url as last known.
    ///
    /// Stored rather than joined at projection time so the incremental flush touches exactly
    /// one table, and so the slug captured ON a freshness event — the slug as it was at the
    /// moment of expiry — is what gets purged and removed, not whatever a later read finds
    /// after someone edited it.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Whether this URL currently belongs in <c>sitemap-jobs</c>.</summary>
    public SeoUrlStatus Status { get; set; }

    /// <summary>company_job.modified_on, emitted as <c>&lt;lastmod&gt;</c>.</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>
    /// Set when this row changes and cleared only once the change has actually reached R2.
    ///
    /// This is what makes the debounce crash-safe (PLAN §6.1). The in-memory coalescing
    /// signal is an optimisation and dies with the process; this column is the durable
    /// record that a flush is owed. A pod killed between the consumer's COMMIT and the flush
    /// finds dirty rows at boot and flushes them, instead of waiting up to a full cron
    /// interval with a withdrawn job still advertised.
    ///
    /// Per-row rather than a single global flag so the diagnostics endpoint can say WHICH
    /// jobs are unflushed, which is the first question anyone asks when a URL is still in
    /// the sitemap after an expiry.
    /// </summary>
    public bool Dirty { get; set; }

    /// <summary>When this row was last touched by this service. Diagnostics only —
    /// deliberately NOT emitted as lastmod, which describes the JOB, not our bookkeeping.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Marks the row live and current, and owing a flush.</summary>
    public void MarkLive(string slug, DateTimeOffset? lastModified, DateTimeOffset now)
    {
        Slug = slug;
        Status = SeoUrlStatus.Live;
        LastModified = lastModified;
        Dirty = true;
        UpdatedAt = now;
    }

    /// <summary>
    /// Marks the row removed. The row is KEPT rather than deleted.
    ///
    /// Deleting it would lose the slug, and the slug is what a later resurrection — or a
    /// repeated purge after a crash — needs to address the job's URLs. It is also the only
    /// local record that this URL was ever advertised, which is where a "why is Google still
    /// showing this?" investigation starts.
    /// </summary>
    public void MarkRemoved(string slug, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            Slug = slug;
        }

        Status = SeoUrlStatus.Removed;
        Dirty = true;
        UpdatedAt = now;
    }
}

/// <summary>Whether a job URL belongs in the sitemap.</summary>
public enum SeoUrlStatus
{
    /// <summary>In the live corpus; emitted into <c>sitemap-jobs</c>.</summary>
    Live = 0,

    /// <summary>Expired, deactivated or gone; excluded from <c>sitemap-jobs</c>.</summary>
    Removed = 1,
}
