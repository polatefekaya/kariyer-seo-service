using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// The EF Core implementation of every database operation.
///
/// Two things here are raw SQL rather than LINQ, deliberately, and both say why where they
/// appear: the facet aggregate (which groups on two array columns EF cannot translate) and
/// the corpus sync (which must be set-based to avoid loading a whole catalogue into memory).
/// Everything else is LINQ, because raw SQL earns its cost only where the operation cannot
/// be expressed otherwise or would otherwise become N round-trips.
/// </summary>
public sealed class SeoStore(
    SeoDbContext db,
    IOptions<PersistenceOptions> persistence,
    ILogger<SeoStore> logger) : ISeoStore
{
    private readonly PersistenceOptions _options = persistence.Value;

    private string CompanyJob => $"{Quote(_options.CompanyJobSchema)}.company_job";

    private string UrlStateTable => $"{Quote(_options.Schema)}.seo_url_state";

    private string CmsPageTable => $"{Quote(_options.CmsSchema)}.seo_page";

    /// <summary>
    /// The indexable-CMS-page predicate, stated ONCE.
    ///
    /// It mirrors <c>Kariyer.Cms.Domain.Pages.PublicationPolicy.IsIndexable</c> in the CMS
    /// service: published, with a published snapshot, and not marked noindex. All three
    /// clauses matter and the middle one is the least obvious — a row marked published with a
    /// null <c>published_layout</c> should be impossible, but advertising its URL would put an
    /// empty page in front of a crawler, so it is cheaper to re-check than to explain.
    ///
    /// Note what is deliberately NOT here: <c>noindex</c> does not make a page unreachable.
    /// A published noindex page is a real page a visitor can open — a campaign or thank-you
    /// page — that we simply ask crawlers to skip. It stays out of the sitemap and stays live.
    ///
    /// This duplicates a rule owned by another service, which is a real cost. It is accepted
    /// because the alternative is an HTTP call on the rebuild path, and
    /// <c>CmsPageProjectionTests</c> pins the two against each other.
    /// </summary>
    private const string CmsIndexablePredicate =
        "p.status = 'published' AND p.published_layout IS NOT NULL AND p.noindex = false";

    /// <summary>
    /// The live-corpus predicate, stated ONCE.
    ///
    /// PLAN §16 defines live as <c>status='approved' AND is_active=true</c>. <c>is_deleted</c>
    /// is added because a soft-deleted row can still carry that pair, and a sitemap entry for
    /// a deleted posting is a 404 advertised to Google. A non-empty slug is required for the
    /// same reason: <c>/is-ilanlari/ilan/</c> is not a page.
    ///
    /// It is one constant rather than four copies so the corpus stream, the facet aggregate,
    /// the count and the sync can never disagree about what "live" means — a divergence that
    /// would surface as facet counts that do not add up to the jobs sitemap, with nothing to
    /// say which side is wrong.
    /// </summary>
    private const string LivePredicate = "j.status = 'approved' AND j.is_active = true "
        + "AND j.is_deleted = false AND j.slug_url IS NOT NULL AND j.slug_url <> ''";

    /// <summary>
    /// <c>company_job</c> aliased as <c>j</c>, so <see cref="LivePredicate"/> can be a single
    /// constant used verbatim everywhere.
    ///
    /// Every query aliases the table rather than some qualifying the columns and others not:
    /// one predicate string that is textually identical at every use site is the only version
    /// of "stated once" that a reviewer can actually verify at a glance.
    /// </summary>
    private string CompanyJobAliased => $"{CompanyJob} j";

    // ── Corpus (read-only) ──────────────────────────────────────────────────────

    public async IAsyncEnumerable<LiveJob> StreamLiveJobsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ORDER BY uid is load-bearing, not cosmetic: it is what keeps a given URL in the
        // same chunk file across rebuilds. Without it Postgres may return rows in whatever
        // order a plan produces, every chunk's checksum would differ on every run, the
        // conditional-write short-circuit would never fire, and the entire set would be
        // re-uploaded every 45 minutes.
        string sql = $"""
            SELECT j.uid, j.slug_url, j.status, j.is_active, j.is_deleted, j.modified_on,
                   j.province, j.town, j.department, j.position, j.working_type, j.working_prefs
              FROM {CompanyJobAliased}
             WHERE {LivePredicate}
             ORDER BY j.uid
            """;

        IAsyncEnumerable<CompanyJobReadModel> rows = db.CompanyJobs
            .FromSqlRaw(sql)
            .AsNoTracking()
            .AsAsyncEnumerable();

        await foreach (CompanyJobReadModel job in rows.WithCancellation(cancellationToken))
        {
            yield return new LiveJob(job.Uid, job.SlugUrl!, job.ModifiedOn);
        }
    }

    public async Task<int> CountLiveJobsAsync(CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(
            $"SELECT COUNT(*)::int FROM {CompanyJobAliased} WHERE {LivePredicate}",
            cancellationToken);

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? 0 : (int)value;
    }

    /// <summary>
    /// The single-pass facet aggregate (PLAN §7).
    ///
    /// Raw SQL because it groups on <c>working_type</c> and <c>working_prefs</c>, which are
    /// <c>text[]</c>. EF cannot translate a GROUP BY over an array column, and both LINQ
    /// alternatives are wrong at this scale: grouping in memory means streaming the whole
    /// corpus back, and unnesting each array into its own aggregate loses the correlation
    /// between axes — <c>count(city) × count(sector)</c> is not <c>count(city AND sector)</c>,
    /// so no combo facet could be answered at all.
    ///
    /// One query. The result set is bounded by DISTINCT tuples (thousands), not by the corpus
    /// (hundreds of thousands), and each of the ~3,000 manifest facets is then a pure
    /// in-memory fold over it.
    /// </summary>
    public async Task<IReadOnlyList<LiveJobFacetTuple>> GetFacetTuplesAsync(
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT j.province, j.department, j.position, j.working_type, j.working_prefs,
                   COUNT(*)::int
              FROM {CompanyJobAliased}
             WHERE {LivePredicate}
             GROUP BY j.province, j.department, j.position, j.working_type, j.working_prefs
            """;

        await using DbCommand command = await CreateCommandAsync(sql, cancellationToken);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<LiveJobFacetTuple> tuples = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            tuples.Add(new LiveJobFacetTuple(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? [] : (string[])reader.GetValue(3),
                reader.IsDBNull(4) ? [] : (string[])reader.GetValue(4),
                reader.GetInt32(5)));
        }

        logger.LogInformation(
            "Facet aggregate returned {Tuples} distinct column combinations in one query.",
            tuples.Count);

        return tuples;
    }

    // ── CMS pages (read-only, another service's schema) ─────────────────────────

    public async IAsyncEnumerable<CmsPage> StreamIndexablePagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ORDER BY path, for the same reason the corpus stream orders by uid: a stable order
        // is what keeps the file's checksum stable when nothing changed.
        //
        // `published_layout IS NOT NULL` is projected as a bool rather than selecting the
        // column, so a rebuild never drags every page's full layout document across the wire
        // to answer a null check.
        string sql = $"""
            SELECT p.path, p.status, p.noindex,
                   (p.published_layout IS NOT NULL) AS has_published_layout,
                   p.published_at
              FROM {CmsPageTable} p
             WHERE {CmsIndexablePredicate}
             ORDER BY p.path
            """;

        IAsyncEnumerable<CmsPageReadModel> rows = db.CmsPages
            .FromSqlRaw(sql)
            .AsNoTracking()
            .AsAsyncEnumerable();

        await foreach (CmsPageReadModel page in rows.WithCancellation(cancellationToken))
        {
            yield return new CmsPage(page.Path, page.PublishedAt);
        }
    }

    public async Task<int> CountIndexablePagesAsync(CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(
            $"SELECT COUNT(*)::int FROM {CmsPageTable} p WHERE {CmsIndexablePredicate}",
            cancellationToken);

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? 0 : (int)value;
    }

    // ── Incremental URL state ───────────────────────────────────────────────────

    public Task<SeoUrlState?> GetUrlStateAsync(string jobUid, CancellationToken ct) =>
        db.UrlStates.FirstOrDefaultAsync(s => s.JobUid == jobUid, ct);

    public async Task<SeoUrlState> UpsertUrlStateAsync(
        string jobUid, string slug, SeoUrlStatus status, DateTimeOffset now, CancellationToken ct)
    {
        SeoUrlState? state = await db.UrlStates.FirstOrDefaultAsync(s => s.JobUid == jobUid, ct);

        if (state is null)
        {
            state = new SeoUrlState { JobUid = jobUid, Slug = slug };
            db.UrlStates.Add(state);
        }

        if (status is SeoUrlStatus.Live)
        {
            // LastModified is carried over rather than stamped with "now". A freshness event
            // carries no modified_on, and claiming the job changed at the moment we happened
            // to process a message is precisely the lastmod inflation that teaches Google to
            // ignore the field. The next full rebuild refreshes it from company_job, which is
            // the only place that actually knows.
            state.MarkLive(slug, state.LastModified, now);
        }
        else
        {
            state.MarkRemoved(slug, now);
        }

        return state;
    }

    public IAsyncEnumerable<SeoUrlState> StreamLiveUrlStatesAsync(CancellationToken ct) =>
        db.UrlStates
            .AsNoTracking()
            .Where(s => s.Status == SeoUrlStatus.Live && s.Slug != string.Empty)
            .OrderBy(s => s.JobUid)
            .AsAsyncEnumerable();

    public Task<int> CountDirtyAsync(CancellationToken ct) =>
        db.UrlStates.CountAsync(s => s.Dirty, ct);

    public Task<int> ClearDirtyAsync(DateTimeOffset asOf, CancellationToken ct) =>
        db.UrlStates
            .Where(s => s.Dirty && s.UpdatedAt <= asOf)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Dirty, false), ct);

    /// <summary>
    /// Re-derives the whole URL projection from <c>company_job</c> — the reconstruction path
    /// of PLAN §6.4.
    ///
    /// Set-based rather than row-by-row: at several hundred thousand jobs, pulling the corpus
    /// and the projection into memory to diff them would cost more than the sitemap build
    /// itself. Two statements do it inside the database, where both tables already live.
    /// </summary>
    public async Task<UrlStateSyncResult> SyncUrlStatesFromCorpusAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        // `dirty` is written as FALSE rather than left alone. This statement IS the input to
        // the projection the caller runs immediately afterwards, so marking these rows as
        // owing a flush would queue a second, identical projection for work about to happen.
        string upsert = $"""
            INSERT INTO {UrlStateTable} (job_uid, slug, status, last_modified, dirty, updated_at)
            SELECT j.uid, j.slug_url, 0, j.modified_on, false, @now
              FROM {CompanyJobAliased}
             WHERE {LivePredicate}
            ON CONFLICT (job_uid) DO UPDATE SET
                slug          = EXCLUDED.slug,
                status        = EXCLUDED.status,
                last_modified = EXCLUDED.last_modified,
                dirty         = false,
                updated_at    = EXCLUDED.updated_at
            """;

        int live = await db.Database.ExecuteSqlRawAsync(
            upsert, [new NpgsqlParameter("now", now)], ct);

        // Anything we still call live that the corpus no longer does becomes removed.
        //
        // This is the backstop for every way a job can leave the corpus WITHOUT a freshness
        // event: an employer deactivating a posting in the panel, an admin deleting one, the
        // Node cron expiring one by end date, a slug being cleared. Freshness only publishes
        // expiries IT performed, so without this clause those URLs would stay advertised
        // indefinitely — and that is the majority of departures, not the minority.
        string retire = $"""
            UPDATE {UrlStateTable} s
               SET status = 1, dirty = false, updated_at = @now
             WHERE s.status = 0
               AND NOT EXISTS (
                     SELECT 1 FROM {CompanyJobAliased}
                      WHERE j.uid = s.job_uid AND {LivePredicate}
                   )
            """;

        int removed = await db.Database.ExecuteSqlRawAsync(
            retire, [new NpgsqlParameter("now", now)], ct);

        return new UrlStateSyncResult(live, removed);
    }

    // ── Facet state ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, bool>> GetFacetIndexabilityAsync(CancellationToken ct)
    {
        List<FacetIndexabilityRow> rows = await db.FacetStates
            .AsNoTracking()
            .Select(f => new FacetIndexabilityRow(f.FacetPath, f.Indexable))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Path, r => r.Indexable, StringComparer.Ordinal);
    }

    public async Task SaveFacetStatesAsync(
        IReadOnlyList<FacetDefinition> facets,
        IReadOnlyDictionary<string, FacetIndexability> computed,
        DateTimeOffset now,
        CancellationToken ct)
    {
        Dictionary<string, SeoFacetState> existing = await db.FacetStates
            .ToDictionaryAsync(f => f.FacetPath, StringComparer.Ordinal, ct);

        HashSet<string> present = new(StringComparer.Ordinal);

        foreach (FacetDefinition facet in facets)
        {
            if (!computed.TryGetValue(facet.Path, out FacetIndexability state))
            {
                continue;
            }

            present.Add(facet.Path);

            if (!existing.TryGetValue(facet.Path, out SeoFacetState? row))
            {
                row = new SeoFacetState { FacetPath = facet.Path };
                db.FacetStates.Add(row);
            }

            row.Axes = facet.Axes;
            row.JobCount = state.JobCount;
            row.Indexable = state.Indexable;
            row.LastModified = now;
        }

        // Facets the manifest no longer lists are DELETED, not left behind. A stale row would
        // keep answering the transition diff with an opinion about a page the web app has
        // stopped treating as a candidate — so if that facet ever returned, the diff would
        // read "no change" and never emit the event announcing it came back.
        foreach ((string path, SeoFacetState row) in existing)
        {
            if (!present.Contains(path))
            {
                db.FacetStates.Remove(row);
            }
        }
    }

    // ── Rebuild log ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, string>> GetLastChecksumsAsync(CancellationToken ct)
    {
        List<ChecksumRow> rows = await db.RebuildLog
            .AsNoTracking()
            .GroupBy(l => l.File)
            .Select(g => new ChecksumRow(
                g.Key,
                g.OrderByDescending(l => l.GeneratedAt).ThenByDescending(l => l.Id)
                 .Select(l => l.Checksum).First()))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.File, r => r.Checksum, StringComparer.Ordinal);
    }

    public void AppendRebuildLog(IEnumerable<SeoRebuildLog> entries) =>
        db.RebuildLog.AddRange(entries);

    public async Task<IReadOnlyList<SeoRebuildLog>> GetRecentRebuildLogAsync(
        int limit, CancellationToken ct) =>
        await db.RebuildLog
            .AsNoTracking()
            .OrderByDescending(l => l.GeneratedAt)
            .ThenByDescending(l => l.Id)
            .Take(limit)
            .ToListAsync(ct);

    // ── Unit of work ────────────────────────────────────────────────────────────

    public async Task<ISeoTransaction> BeginTransactionAsync(CancellationToken ct) =>
        new EfTransaction(await db.Database.BeginTransactionAsync(ct));

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // ── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a command on the DbContext's OWN connection, enlisted in whatever transaction
    /// the caller has open.
    ///
    /// Both halves matter. The context's connection, so a raw read sees the same snapshot as
    /// the LINQ around it — a rebuild computing facet counts on one connection and the URL
    /// list on another would describe two different moments and produce a set whose parts
    /// disagree. And the explicit enlistment, because ADO.NET silently runs a command OUTSIDE
    /// the ambient transaction if it is not told about it.
    /// </summary>
    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken ct)
    {
        System.Data.Common.DbConnection connection = db.Database.GetDbConnection();

        if (connection.State is not ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    /// <summary>
    /// Quotes a schema identifier.
    ///
    /// Schema names come from configuration, not from user input, but they are still
    /// interpolated into SQL — so they are quoted and rejected rather than trusted. The cost
    /// is one method; the alternative is an injection point that only a deployment change
    /// can reach, which is exactly the kind nobody tests.
    /// </summary>
    private static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Contains('"'))
        {
            throw new InvalidOperationException(
                $"Invalid schema identifier '{identifier}'. Schema names are interpolated into "
                + "SQL and must not contain a double quote.");
        }

        return $"\"{identifier}\"";
    }

    private sealed class EfTransaction(IDbContextTransaction transaction) : ISeoTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    // Named projection types rather than anonymous ones, so EF's translation of the Select
    // is unambiguous and the shapes are visible in a stack trace.
    private sealed record FacetIndexabilityRow(string Path, bool Indexable);

    private sealed record ChecksumRow(string File, string Checksum);
}
