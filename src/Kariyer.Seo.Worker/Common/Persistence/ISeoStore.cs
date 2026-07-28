using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// Every database operation this service performs, in one reviewable place.
///
/// It extends <see cref="IJobCorpusReader"/> rather than duplicating it, so the domain sees
/// only the read surface it needs and the Worker sees the whole thing. Note what is absent
/// and always will be: any method that writes <c>company_job</c>.
/// </summary>
public interface ISeoStore : IJobCorpusReader, ICmsPageReader
{
    // ── Incremental URL state ───────────────────────────────────────────────────

    /// <summary>Loads one job's URL state, tracked for update.</summary>
    Task<SeoUrlState?> GetUrlStateAsync(string jobUid, CancellationToken ct);

    /// <summary>
    /// Applies a freshness event to the local projection, creating the row if this service
    /// has not seen the job before.
    ///
    /// Creating on the removed path matters: a job expired before this service's first full
    /// rebuild has no row, and skipping it would mean the removal is only recorded when the
    /// next rebuild happens to read the corpus — by which time <c>company_job</c> already
    /// says expired, so the URL leaves the sitemap, but the PURGE that should have happened
    /// on the event never does and the prerendered page survives its whole TTL.
    /// </summary>
    Task<SeoUrlState> UpsertUrlStateAsync(
        string jobUid, string slug, SeoUrlStatus status, DateTimeOffset now, CancellationToken ct);

    /// <summary>Live URLs for the incremental projection, in a stable order.</summary>
    IAsyncEnumerable<SeoUrlState> StreamLiveUrlStatesAsync(CancellationToken ct);

    /// <summary>How many rows are awaiting a flush. Drives boot recovery and diagnostics.</summary>
    Task<int> CountDirtyAsync(CancellationToken ct);

    /// <summary>
    /// Clears the dirty flag for rows that were dirty at or before <paramref name="asOf"/>.
    ///
    /// Bounded by a timestamp rather than clearing everything, because a consumer can commit
    /// a new change WHILE a flush is streaming: clearing unconditionally afterwards would
    /// mark that change flushed when the projection it landed in had already been read past,
    /// and the job would sit in the wrong state until the next full rebuild.
    /// </summary>
    Task<int> ClearDirtyAsync(DateTimeOffset asOf, CancellationToken ct);

    /// <summary>
    /// Replaces the whole URL projection from the live corpus: everything present becomes
    /// live, everything absent becomes removed. The reconstruction path of PLAN §6.4.
    /// </summary>
    Task<UrlStateSyncResult> SyncUrlStatesFromCorpusAsync(DateTimeOffset now, CancellationToken ct);

    // ── Facet state ─────────────────────────────────────────────────────────────

    /// <summary>Last recorded indexability per facet path, for the transition diff.</summary>
    Task<IReadOnlyDictionary<string, bool>> GetFacetIndexabilityAsync(CancellationToken ct);

    /// <summary>Replaces the recorded facet state with what this rebuild computed.</summary>
    Task SaveFacetStatesAsync(
        IReadOnlyList<FacetDefinition> facets,
        IReadOnlyDictionary<string, FacetIndexability> computed,
        DateTimeOffset now,
        CancellationToken ct);

    // ── Rebuild log ─────────────────────────────────────────────────────────────

    /// <summary>Newest checksum per file, for the conditional-write short-circuit.</summary>
    Task<IReadOnlyDictionary<string, string>> GetLastChecksumsAsync(CancellationToken ct);

    /// <summary>Appends rebuild-log rows. Not committed until the caller saves.</summary>
    void AppendRebuildLog(IEnumerable<SeoRebuildLog> entries);

    /// <summary>Most recent log rows, newest first, for the diagnostics endpoint.</summary>
    Task<IReadOnlyList<SeoRebuildLog>> GetRecentRebuildLogAsync(int limit, CancellationToken ct);

    // ── Unit of work ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a transaction spanning local state and the outbox message that announces it —
    /// the two things that must commit together or not at all.
    /// </summary>
    Task<ISeoTransaction> BeginTransactionAsync(CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A unit of work spanning this service's state and its outbox.</summary>
public interface ISeoTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

/// <summary>What a full corpus sync changed.</summary>
/// <param name="Live">Rows now marked live.</param>
/// <param name="Removed">
/// Rows flipped to removed because the corpus no longer contains them.
///
/// Worth logging on every rebuild: a large jump here means either a genuine catalogue event
/// or that this service's idea of "live" has drifted from the jobs API's, and the two are
/// told apart by whether the number is plausible.
/// </param>
public readonly record struct UrlStateSyncResult(int Live, int Removed);
