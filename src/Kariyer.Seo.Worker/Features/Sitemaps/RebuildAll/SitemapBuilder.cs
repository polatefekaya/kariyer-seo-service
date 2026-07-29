using System.Diagnostics;
using Kariyer.Messaging.Contracts.Seo;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Robots;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Telemetry;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;

/// <summary>
/// The full-corpus rebuild (PLAN §4) — the correctness backstop of the whole service.
///
/// Everything else here is an optimisation. The incremental path exists so a withdrawn job
/// leaves the sitemap in seconds rather than in up to 45 minutes; this is what makes the
/// sitemap actually CORRECT, by re-deriving it from <c>company_job</c> and throwing away
/// whatever the local projection thought. That is the concrete meaning of PLAN §0's promise:
/// truth is always one query away, so the worst this service can produce is staleness bounded
/// by the cron interval.
///
/// The sequence, and why it is this order:
/// <list type="number">
///   <item>Sync <c>seo_url_state</c> from the corpus — so the incremental path starts from
///   truth again, and so anything that left the catalogue WITHOUT a freshness event (an
///   employer deactivating a posting, the Node end-date cron, a deletion) is caught. That is
///   the majority of departures, not the minority.</item>
///   <item>Fetch the facet manifest and run the single-pass aggregate.</item>
///   <item>Write every file into a STAGE, computing checksums as the bytes stream past.</item>
///   <item>Compare against the last checksums; skip files that are byte-identical.</item>
///   <item>Commit the stage — children first, index last (PLAN §6.3).</item>
///   <item>Only then write the log rows and publish, in ONE transaction through the outbox.</item>
/// </list>
///
/// Step 6 is last for a reason that is easy to get backwards: the event says "this is live",
/// so publishing before the swap would announce a state that does not exist yet and that a
/// crash would prevent from ever existing.
/// </summary>
public sealed class SitemapBuilder(
    ISeoStore store,
    ISitemapSink sink,
    IFacetManifestSource manifest,
    IPublishEndpoint publisher,
    IOptions<SeoOptions> options,
    TimeProvider clock,
    ILogger<SitemapBuilder> logger)
{
    private SeoOptions Seo => options.Value;

    public async Task<RebuildOutcome> RebuildAsync(string trigger, CancellationToken cancellationToken)
    {
        using Activity? activity = DiagnosticsConfig.ActivitySource.StartActivity("SitemapRebuild");
        activity?.SetTag("seo.trigger", trigger);

        long startedAt = Stopwatch.GetTimestamp();
        DateTimeOffset now = clock.GetUtcNow();

        // ── 1. Re-derive the local projection from the corpus ───────────────────
        UrlStateSyncResult sync = await store.SyncUrlStatesFromCorpusAsync(now, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Corpus sync: {Live} live URLs upserted, {Removed} retired. Retired jobs that never "
            + "produced a freshness event are the ones only this path can catch.",
            sync.Live, sync.Removed);

        // ── 2. Facets ───────────────────────────────────────────────────────────
        IReadOnlyList<FacetDefinition> facets = await manifest.GetAsync(cancellationToken);

        IReadOnlyList<LiveJobFacetTuple> tuples = await store.GetFacetTuplesAsync(cancellationToken);

        IReadOnlyDictionary<string, int> facetCounts = FacetCountProjector.Project(facets, tuples);

        IndexationThresholds thresholds = new(Seo.Thresholds.SingleAxis, Seo.Thresholds.Combo);

        Dictionary<string, FacetIndexability> computed = facets.ToDictionary(
            f => f.Path,
            f =>
            {
                int count = facetCounts.TryGetValue(f.Path, out int c) ? c : 0;
                return new FacetIndexability(
                    IndexationPolicy.IsIndexable(f, count, thresholds), count);
            },
            StringComparer.Ordinal);

        List<FacetDefinition> indexable =
            [.. facets.Where(f => computed[f.Path].Indexable).OrderBy(f => f.Path, StringComparer.Ordinal)];

        DiagnosticsConfig.IndexableFacets.Record(indexable.Count);

        logger.LogInformation(
            "{Indexable} of {Candidates} candidate facets cleared their threshold "
            + "(single-axis ≥ {Single}, combo ≥ {Combo}).",
            indexable.Count, facets.Count, thresholds.SingleAxis, thresholds.Combo);

        // ── 3–5. Build, compare, swap ───────────────────────────────────────────
        //
        // TWO checksum sources, and a file is skipped only when both agree.
        //
        // seo_rebuild_log is the transactional record of what we published. R2 object
        // metadata is what is actually THERE. Trusting the log alone would leave a file
        // permanently missing from a bucket restored from backup — the log would say
        // "already published" forever and the index would name a 404 no rebuild repairs.
        // Trusting R2 alone would re-upload everything whenever a metadata read failed.
        IReadOnlyDictionary<string, string> lastChecksums =
            await store.GetLastChecksumsAsync(cancellationToken);

        IReadOnlyDictionary<string, string> liveChecksums =
            await sink.GetLiveChecksumsAsync(cancellationToken);

        List<BuiltFile> built;

        await using (ISitemapStage stage = await sink.BeginAsync(cancellationToken))
        {
            built = await WriteAllAsync(stage, indexable, lastChecksums, liveChecksums, cancellationToken);

            foreach (BuiltFile file in built)
            {
                stage.RecordChecksum(file.Chunk.FileName, file.Chunk.Checksum);

                if (!file.Changed)
                {
                    stage.MarkUnchanged(file.Chunk.FileName);
                }
            }

            // Which previously-live files this set no longer contains. A corpus that shrank
            // below a 50k boundary loses a chunk, and leaving it live would keep a file of
            // stale URLs on R2 that the index no longer names but crawlers still remember.
            //
            // Derived from what is ACTUALLY in the bucket rather than from the log, so a file
            // written by an older build — a chunk from before a naming change, say — is
            // cleaned up too.
            HashSet<string> current = [.. built.Select(f => f.Chunk.FileName)];

            IReadOnlyCollection<string> obsolete =
            [
                .. liveChecksums.Keys
                    .Concat(lastChecksums.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .Where(f => !current.Contains(f) && SitemapNames.IsOwned(f)),
            ];

            await stage.CommitAsync(SitemapNames.Index, obsolete, cancellationToken);
        }

        // ── 6. Log rows, facet state and events — one commit ────────────────────
        await using ISeoTransaction transaction = await store.BeginTransactionAsync(cancellationToken);

        IReadOnlyDictionary<string, bool> previousIndexability =
            await store.GetFacetIndexabilityAsync(cancellationToken);

        store.AppendRebuildLog(built.Select(f => new SeoRebuildLog
        {
            File = f.Chunk.FileName,
            SitemapType = f.SitemapType,
            UrlCount = f.Chunk.UrlCount,
            Checksum = f.Chunk.Checksum,
            UncompressedBytes = f.Chunk.UncompressedBytes,
            Uploaded = f.Changed,
            GeneratedAt = now,
        }));

        await store.SaveFacetStatesAsync(facets, computed, now, cancellationToken);

        // One event per file that actually CHANGED.
        //
        // Publishing for unchanged files too would make this exchange a heartbeat: several
        // messages every 45 minutes forever, of which essentially none carry news, and
        // "the jobs sitemap changed" would stop being something a subscriber could act on.
        foreach (BuiltFile file in built.Where(f => f.Changed))
        {
            await publisher.Publish(
                new SitemapRebuiltEvent
                {
                    // Derived from the content, not random. A redelivered or replayed outbox
                    // message then carries the SAME id, so a consumer can recognise the
                    // duplicate — which a broker-assigned MessageId, regenerated on every
                    // redelivery, could not let it do.
                    MessageId = $"{file.Chunk.FileName}:{file.Chunk.Checksum}",
                    SitemapType = file.SitemapType,
                    UrlCount = file.Chunk.UrlCount,
                    Checksum = file.Chunk.Checksum,
                    GeneratedAt = now,
                },
                cancellationToken);
        }

        IReadOnlyList<FacetIndexabilityChange> transitions =
            FacetIndexabilityTracker.Diff(computed, previousIndexability);

        foreach (FacetIndexabilityChange change in transitions)
        {
            DiagnosticsConfig.FacetTransitions.Add(1,
                new KeyValuePair<string, object?>("direction", change.Indexable ? "in" : "out"));

            await publisher.Publish(
                new FacetIndexabilityChangedEvent
                {
                    MessageId = $"{change.FacetPath}:{now:O}",
                    FacetPath = change.FacetPath,
                    Indexable = change.Indexable,
                    JobCount = change.JobCount,
                    ChangedAt = now,
                },
                cancellationToken);
        }

        // Dirty flags cleared as of the sync instant, not "now": a consumer may have
        // committed a change WHILE this rebuild was streaming, and that change is not in the
        // file we just published.
        await store.ClearDirtyAsync(now, cancellationToken);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        DiagnosticsConfig.Rebuilds.Add(1, new KeyValuePair<string, object?>("trigger", trigger));
        DiagnosticsConfig.RebuildDuration.Record(elapsed.TotalSeconds);

        int changed = built.Count(f => f.Changed);

        logger.LogInformation(
            "Rebuild complete in {Elapsed:0.0}s: {Files} files, {Changed} changed, "
            + "{Skipped} unchanged, {Transitions} facet transitions.",
            elapsed.TotalSeconds, built.Count, changed, built.Count - changed, transitions.Count);

        return new RebuildOutcome(built.Count, changed, indexable.Count, transitions.Count, elapsed);
    }

    /// <summary>
    /// Writes every file of the set into the stage.
    ///
    /// Everything is written even when nothing changed, and only the UPLOAD is skipped. That
    /// is not wasted work: the checksum can only be known by producing the bytes, so
    /// "produce, then compare" is the only order available. What it saves is the network and
    /// the object write, which is the expensive half — and the log row it produces records
    /// that this run verified the file rather than merely not touching it.
    /// </summary>
    private async Task<List<BuiltFile>> WriteAllAsync(
        ISitemapStage stage,
        IReadOnlyList<FacetDefinition> indexableFacets,
        IReadOnlyDictionary<string, string> lastChecksums,
        IReadOnlyDictionary<string, string> liveChecksums,
        CancellationToken cancellationToken)
    {
        List<BuiltFile> built = [];
        List<SitemapIndexEntry> children = [];

        // ── Jobs ────────────────────────────────────────────────────────────────
        //
        // Read from seo_url_state, not straight from company_job. Both are correct at this
        // instant — step 1 just synced them — but going through the projection means the
        // full rebuild and the incremental flush produce their file by the SAME code path.
        // Two paths that must agree byte for byte and are exercised differently is how a
        // divergence lives for months.
        IReadOnlyList<SitemapChunk> jobChunks = SitemapWriter.WriteUrlSets(
            SitemapNames.JobsBase,
            LiveJobUrlSource.Enumerate(store, Seo.SiteUrl, cancellationToken),
            fileName => stage.OpenWrite(fileName, "application/xml"));

        Record(built, children, jobChunks, SitemapNames.Types.Jobs, lastChecksums, liveChecksums);

        DiagnosticsConfig.SitemapUrlCount.Record(
            jobChunks.Sum(c => c.UrlCount),
            new KeyValuePair<string, object?>("sitemap_type", SitemapNames.Types.Jobs));

        // ── Job filters ─────────────────────────────────────────────────────────
        //
        // No <lastmod>. This service knows how many live jobs a facet has, not when its
        // rendered page last changed — and a facet page changes whenever ANY of its jobs
        // does, which is far more often than a rebuild could observe. Stamping the rebuild
        // time would claim every facet changed every 45 minutes and teach the crawler to
        // ignore the field on the file where it is most useful.
        IReadOnlyList<SitemapChunk> facetChunks = SitemapWriter.WriteUrlSets(
            SitemapNames.JobFiltersBase,
            indexableFacets.Select(f => SitemapUrl.At(FacetUrl.For(Seo.SiteUrl, f.Path))),
            fileName => stage.OpenWrite(fileName, "application/xml"));

        Record(built, children, facetChunks, SitemapNames.Types.JobFilters, lastChecksums, liveChecksums);

        DiagnosticsConfig.SitemapUrlCount.Record(
            facetChunks.Sum(c => c.UrlCount),
            new KeyValuePair<string, object?>("sitemap_type", SitemapNames.Types.JobFilters));

        // ── CMS pages ───────────────────────────────────────────────────────────
        //
        // Read straight from cms.seo_page, a table kariyer-cms-service owns. This is the
        // backstop half of that integration: the events give latency, but a page published
        // while the broker was down would never appear if the sitemap depended on them alone.
        // Truth stays one query away, exactly as it does for jobs.
        //
        // <lastmod> is published_at, not updated_at — a draft save changes nothing a crawler
        // can see.
        IReadOnlyList<SitemapChunk> pageChunks = SitemapWriter.WriteUrlSets(
            SitemapNames.PagesBase,
            CmsPageUrlSource.Enumerate(store, Seo.SiteUrl, cancellationToken),
            fileName => stage.OpenWrite(fileName, "application/xml"));

        Record(built, children, pageChunks, SitemapNames.Types.Pages, lastChecksums, liveChecksums);

        DiagnosticsConfig.SitemapUrlCount.Record(
            pageChunks.Sum(c => c.UrlCount),
            new KeyValuePair<string, object?>("sitemap_type", SitemapNames.Types.Pages));

        // ── Static ──────────────────────────────────────────────────────────────
        IReadOnlyList<SitemapChunk> staticChunks = SitemapWriter.WriteUrlSets(
            SitemapNames.StaticBase,
            Seo.StaticPaths.Select(p => SitemapUrl.At(SiteUrls.Absolute(Seo.SiteUrl, p))),
            fileName => stage.OpenWrite(fileName, "application/xml"));

        Record(built, children, staticChunks, SitemapNames.Types.Static, lastChecksums, liveChecksums);

        // ── Index ───────────────────────────────────────────────────────────────
        SitemapChunk index;

        await using (Stream destination = stage.OpenWrite(SitemapNames.Index, "application/xml"))
        {
            index = SitemapWriter.WriteIndex(SitemapNames.Index, children, destination);
        }

        built.Add(new BuiltFile(
            index, SitemapNames.Types.Index, Changed(index, lastChecksums, liveChecksums)));

        // ── robots.txt ──────────────────────────────────────────────────────────
        //
        // Rebuilt with the set rather than deployed once, so the Sitemap: line and the index
        // that actually exists can never drift apart.
        SitemapChunk robots;

        await using (Stream destination = stage.OpenWrite(SitemapNames.Robots, "text/plain"))
        {
            robots = SitemapWriter.WriteText(
                SitemapNames.Robots,
                RobotsPolicy.Build(
                    Seo.SiteUrl, "/" + SitemapNames.Index, Seo.DisallowedPaths, Seo.AllowIndexing),
                destination);
        }

        built.Add(new BuiltFile(
            robots, SitemapNames.Types.Static, Changed(robots, lastChecksums, liveChecksums)));

        foreach (BuiltFile file in built.Where(f => !f.Changed))
        {
            DiagnosticsConfig.ChecksumSkips.Add(1,
                new KeyValuePair<string, object?>("file", file.Chunk.FileName));
        }

        return built;
    }

    private void Record(
        List<BuiltFile> built,
        List<SitemapIndexEntry> children,
        IReadOnlyList<SitemapChunk> chunks,
        string sitemapType,
        IReadOnlyDictionary<string, string> lastChecksums,
        IReadOnlyDictionary<string, string> liveChecksums)
    {
        foreach (SitemapChunk chunk in chunks)
        {
            built.Add(new BuiltFile(chunk, sitemapType, Changed(chunk, lastChecksums, liveChecksums)));

            // The index points at the STORED name, including the .gz suffix when compressed —
            // that is the URL a crawler will actually request. Naming the uncompressed file
            // here would produce an index of 404s.
            string stored = Seo.R2.Compress ? chunk.FileName + ".gz" : chunk.FileName;

            children.Add(new SitemapIndexEntry(
                SiteUrls.Absolute(Seo.SiteUrl, "/" + stored), chunk.NewestLastModified));
        }
    }

    /// <summary>
    /// A file is UNCHANGED only when our transactional record and the live object BOTH
    /// carry this exact checksum. Any disagreement, any missing entry, any unreadable
    /// metadata falls through to "changed" — the safe direction, because the cost of
    /// re-uploading a file that did not need it is a few hundred kilobytes, and the cost of
    /// skipping one that did is an index pointing at content that never got published.
    /// </summary>
    private static bool Changed(
        SitemapChunk chunk,
        IReadOnlyDictionary<string, string> last,
        IReadOnlyDictionary<string, string> live) =>
        !last.TryGetValue(chunk.FileName, out string? recorded)
        || !live.TryGetValue(chunk.FileName, out string? published)
        || !string.Equals(recorded, chunk.Checksum, StringComparison.Ordinal)
        || !string.Equals(published, chunk.Checksum, StringComparison.Ordinal);


    private readonly record struct BuiltFile(SitemapChunk Chunk, string SitemapType, bool Changed);
}

/// <summary>What one rebuild did, for the log line and the diagnostics endpoint.</summary>
public readonly record struct RebuildOutcome(
    int Files,
    int Changed,
    int IndexableFacets,
    int FacetTransitions,
    TimeSpan Elapsed);
