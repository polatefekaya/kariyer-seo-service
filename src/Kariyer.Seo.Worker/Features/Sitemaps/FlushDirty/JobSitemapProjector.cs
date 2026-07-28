using Kariyer.Messaging.Contracts.Seo;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Telemetry;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;

/// <summary>
/// Re-projects the two files an inbound event can change — <c>sitemap-jobs</c> from
/// <c>seo_url_state</c> and <c>sitemap-pages</c> from <c>cms.seo_page</c> — then stages and
/// swaps them (PLAN §4).
///
/// <b>Why not the facet or static files.</b> A freshness event moves a facet's live count by
/// one, but a facet only enters or leaves the sitemap when it crosses a threshold — a rare
/// event a 45-minute cron catches perfectly well. Recomputing all ~3,000 facet counts on every
/// expiry would run the aggregate hundreds of times a day to discover, almost always, that
/// nothing changed. Static paths are config and cannot change without a deploy.
///
/// <b>Why pages are re-read rather than tracked.</b> Jobs need <c>seo_url_state</c> because
/// the corpus is hundreds of thousands of rows and re-reading it per event would be absurd.
/// CMS pages number in the tens or hundreds and <c>cms.seo_page</c> is in the same database,
/// so the projection reads it directly every flush. That is why the CMS consumers write no
/// local state at all: there is nothing to cache that is cheaper than the truth.
///
/// <b>Why the index is rewritten too.</b> The index carries each child's <c>&lt;lastmod&gt;</c>.
/// Leaving it alone after replacing a child would advertise a stale timestamp for a file that
/// just changed, which is the one signal a crawler uses to decide whether re-fetching is
/// worth it — so the jobs file would be updated and Google would have no reason to look.
/// </summary>
public sealed class JobSitemapProjector(
    ISeoStore store,
    ISitemapSink sink,
    IPublishEndpoint publisher,
    IOptions<SeoOptions> options,
    TimeProvider clock,
    ILogger<JobSitemapProjector> logger) : IDirtyFlusher
{
    private SeoOptions Seo => options.Value;

    /// <summary>Re-projects and swaps. Returns how many URLs the new file holds.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // NOT an early return when there are no dirty job rows.
        //
        // A CMS page publish raises the same signal and leaves no dirty flag — cms.seo_page is
        // the truth and there is nothing local to mark. Returning here on `dirty == 0` would
        // therefore make every page publish a no-op, and CMS pages would only ever reach the
        // sitemap on the 45-minute cron. The page projection below is cheap (tens of rows) and
        // the checksum short-circuit decides whether anything is actually uploaded.
        int dirty = await store.CountDirtyAsync(cancellationToken);

        IReadOnlyDictionary<string, string> lastChecksums =
            await store.GetLastChecksumsAsync(cancellationToken);

        IReadOnlyDictionary<string, string> liveChecksums =
            await sink.GetLiveChecksumsAsync(cancellationToken);

        List<SitemapChunk> jobChunks = [];
        List<SitemapChunk> pageChunks = [];
        List<SitemapChunk> changed = [];

        await using (ISitemapStage stage = await sink.BeginAsync(cancellationToken))
        {
            // Jobs only when something actually flagged them. This is the expensive half —
            // a full stream of the live corpus — and a CMS-only signal must not pay for it.
            if (dirty > 0)
            {
                jobChunks =
                [
                    .. SitemapWriter.WriteUrlSets(
                        SitemapNames.JobsBase,
                        LiveJobUrlSource.Enumerate(store, Seo.SiteUrl, cancellationToken),
                        fileName => stage.OpenWrite(fileName, "application/xml")),
                ];
            }

            // Pages every time. Tens of rows from a table in the same database; cheaper than
            // the bookkeeping that would let us skip it.
            pageChunks =
            [
                .. SitemapWriter.WriteUrlSets(
                    SitemapNames.PagesBase,
                    CmsPageUrlSource.Enumerate(store, Seo.SiteUrl, cancellationToken),
                    fileName => stage.OpenWrite(fileName, "application/xml")),
            ];

            foreach (SitemapChunk chunk in jobChunks.Concat(pageChunks))
            {
                stage.RecordChecksum(chunk.FileName, chunk.Checksum);

                if (IsUnchanged(chunk, lastChecksums, liveChecksums))
                {
                    stage.MarkUnchanged(chunk.FileName);
                }
                else
                {
                    changed.Add(chunk);
                }
            }

            if (changed.Count == 0)
            {
                // Nothing about the FILE differs, even though rows were marked dirty — a job
                // was expired and resurrected inside one debounce window, say. The flags are
                // still cleared below, because they have genuinely been accounted for.
                logger.LogInformation(
                    "Flush produced no file change; {Dirty} dirty job row(s) cancelled out and "
                    + "the CMS pages were already current.", dirty);
            }
            else
            {
                // The index is rebuilt from the CURRENT children rather than from what this
                // flush produced alone: it names the facet and static files too, and an index
                // listing only the jobs chunks would de-list every other sitemap in the set.
                await WriteIndexAsync(stage, jobChunks, pageChunks, cancellationToken);
            }

            await stage.CommitAsync(SitemapNames.Index, [], cancellationToken);
        }

        await using ISeoTransaction transaction = await store.BeginTransactionAsync(cancellationToken);

        // Each chunk logged under its OWN type. Tagging a pages chunk as `jobs` would corrupt
        // the per-type URL gauge and make the checksum lookup — which keys on file name — read
        // correctly while every dashboard read wrong.
        store.AppendRebuildLog(
            jobChunks.Select(c => LogRow(c, SitemapNames.Types.Jobs, changed, now))
                .Concat(pageChunks.Select(c => LogRow(c, SitemapNames.Types.Pages, changed, now))));

        foreach (SitemapChunk chunk in changed)
        {
            await publisher.Publish(
                new SitemapRebuiltEvent
                {
                    MessageId = $"{chunk.FileName}:{chunk.Checksum}",
                    SitemapType = TypeOf(chunk),
                    UrlCount = chunk.UrlCount,
                    Checksum = chunk.Checksum,
                    GeneratedAt = now,
                },
                cancellationToken);
        }

        // Cleared as of `now`, which was captured BEFORE the projection streamed. A change
        // committed while the file was being written may not be in it, and its row's
        // updated_at is later than this — so it stays dirty and gets the next flush.
        await store.ClearDirtyAsync(now, cancellationToken);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        int jobUrls = jobChunks.Sum(c => c.UrlCount);
        int pageUrls = pageChunks.Sum(c => c.UrlCount);

        DiagnosticsConfig.DirtyFlushes.Add(1);

        if (jobChunks.Count > 0)
        {
            DiagnosticsConfig.SitemapUrlCount.Record(
                jobUrls, new KeyValuePair<string, object?>("sitemap_type", SitemapNames.Types.Jobs));
        }

        DiagnosticsConfig.SitemapUrlCount.Record(
            pageUrls, new KeyValuePair<string, object?>("sitemap_type", SitemapNames.Types.Pages));

        logger.LogInformation(
            "Flushed {Dirty} dirty job row(s): sitemap-jobs holds {JobUrls} URLs across "
            + "{JobChunks} file(s), sitemap-pages holds {PageUrls}; {Changed} file(s) changed.",
            dirty, jobUrls, jobChunks.Count, pageUrls, changed.Count);

        return jobUrls + pageUrls;
    }

    private static SeoRebuildLog LogRow(
        SitemapChunk chunk, string sitemapType, List<SitemapChunk> changed, DateTimeOffset now) =>
        new()
        {
            File = chunk.FileName,
            SitemapType = sitemapType,
            UrlCount = chunk.UrlCount,
            Checksum = chunk.Checksum,
            UncompressedBytes = chunk.UncompressedBytes,
            Uploaded = changed.Any(x => x.FileName == chunk.FileName),
            GeneratedAt = now,
        };

    private static string TypeOf(SitemapChunk chunk) =>
        chunk.FileName.StartsWith(SitemapNames.PagesBase, StringComparison.Ordinal)
            ? SitemapNames.Types.Pages
            : SitemapNames.Types.Jobs;

    /// <summary>
    /// Rewrites the index from the chunks this flush produced plus whatever the last full
    /// rebuild logged for the files it did not touch.
    ///
    /// The rebuild log is the source for the others because this flush has not built them and
    /// must not: it would have to fetch the manifest and run the aggregate to do so, which is
    /// the expensive work the incremental path exists to avoid. Anything this flush DID build
    /// takes precedence, so a stale log row can never win over a file that just changed.
    /// </summary>
    private async Task WriteIndexAsync(
        ISitemapStage stage,
        IReadOnlyList<SitemapChunk> jobChunks,
        IReadOnlyList<SitemapChunk> pageChunks,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SeoRebuildLog> recent = await store.GetRecentRebuildLogAsync(200, cancellationToken);

        // Newest row per file, so a file rebuilt many times contributes once.
        Dictionary<string, SeoRebuildLog> newest = new(StringComparer.Ordinal);

        foreach (SeoRebuildLog row in recent)
        {
            newest.TryAdd(row.File, row);
        }

        List<SitemapIndexEntry> children = [];
        HashSet<string> justBuilt = new(StringComparer.Ordinal);

        foreach (SitemapChunk chunk in jobChunks.Concat(pageChunks))
        {
            children.Add(Entry(chunk.FileName, chunk.NewestLastModified));
            justBuilt.Add(chunk.FileName);
        }

        // Everything else the last rebuild published, EXCLUDING whatever this flush just
        // rebuilt — those already carry a fresher lastmod above.
        //
        // The exclusion is what makes a partial flush safe. A CMS-only signal leaves
        // `jobChunks` empty, and an index built from the freshly-made chunks alone would omit
        // sitemap-jobs entirely: the whole job catalogue would vanish from the index the
        // moment an editor published a landing page. Rebuilding the index means rebuilding
        // ALL of it, from whichever source is currently authoritative per file.
        foreach (SeoRebuildLog row in newest.Values
                     .Where(r => r.File != SitemapNames.Robots)
                     .Where(r => r.File != SitemapNames.Index)
                     .Where(r => !justBuilt.Contains(r.File))
                     .Where(r => SitemapNames.IsOwned(r.File))
                     .OrderBy(r => r.File, StringComparer.Ordinal))
        {
            // No lastmod for these. The rebuild log records when we generated the file, not
            // when the pages inside it changed, and the two are not the same claim — see the
            // note on facet lastmod in SitemapBuilder.
            children.Add(Entry(row.File, null));
        }

        await using Stream destination = stage.OpenWrite(SitemapNames.Index, "application/xml");

        SitemapChunk index = SitemapWriter.WriteIndex(SitemapNames.Index, children, destination);

        stage.RecordChecksum(index.FileName, index.Checksum);
    }

    private SitemapIndexEntry Entry(string fileName, DateTimeOffset? lastModified)
    {
        string stored = Seo.R2.Compress ? fileName + ".gz" : fileName;

        return new SitemapIndexEntry(SiteUrls.Absolute(Seo.SiteUrl, "/" + stored), lastModified);
    }

    private static bool IsUnchanged(
        SitemapChunk chunk,
        IReadOnlyDictionary<string, string> last,
        IReadOnlyDictionary<string, string> live) =>
        last.TryGetValue(chunk.FileName, out string? recorded)
        && live.TryGetValue(chunk.FileName, out string? published)
        && string.Equals(recorded, chunk.Checksum, StringComparison.Ordinal)
        && string.Equals(published, chunk.Checksum, StringComparison.Ordinal);

}
