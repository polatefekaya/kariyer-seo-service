namespace Kariyer.Seo.Domain.Ports;

/// <summary>
/// Where built sitemaps go. Implemented in the Worker against R2 (S3 API); substituted for
/// an in-memory fake in the integration tests.
///
/// The shape of this interface IS the atomicity guarantee of PLAN §6.3. There is no
/// "upload this file" method, because such a method makes a torn set the default outcome:
/// a rebuild that fails after two of five chunks leaves a live index pointing at three
/// stale children and two new ones, and a crawler that fetches during the window sees a
/// catalogue that never existed.
///
/// Instead a caller opens a STAGE, writes every file into it, and either commits the whole
/// set or abandons it. Nothing a partially-written stage produces is reachable by a crawler,
/// so the failure mode is "the sitemap is up to 45 minutes stale" — which is exactly the
/// bound PLAN §0 promises — rather than "the sitemap is wrong".
/// </summary>
public interface ISitemapSink
{
    /// <summary>Begins a staged write. Nothing written into it is publicly visible until
    /// <see cref="ISitemapStage.CommitAsync"/> returns.</summary>
    Task<ISitemapStage> BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checksums of the currently live files, keyed by file name, for the conditional-write
    /// short-circuit (PLAN §7). Most cron ticks change nothing.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLiveChecksumsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One staged, all-or-nothing publication of a sitemap set.
///
/// Disposing without committing abandons the stage — which is what makes "the process died
/// mid-rebuild" safe: the live set is untouched and the next cron tick simply redoes the
/// work.
/// </summary>
public interface ISitemapStage : IAsyncDisposable
{
    /// <summary>
    /// Opens a writable stream for one file in the stage.
    ///
    /// The returned stream is what <see cref="Sitemaps.SitemapWriter"/> writes into, so the
    /// implementation is free to layer gzip and a multipart upload underneath and never hold
    /// the document in memory. Disposing the stream finishes that upload.
    /// </summary>
    /// <param name="fileName">Logical name without any compression suffix, e.g.
    /// <c>sitemap-jobs-1.xml</c>. The sink decides the stored key and whether it is gzipped.</param>
    /// <param name="contentType">MIME type to store on the object.</param>
    Stream OpenWrite(string fileName, string contentType);

    /// <summary>
    /// Records the checksum the writer computed for a file, so the sink can store it
    /// alongside the object.
    ///
    /// Separate from <see cref="OpenWrite"/> because the value does not exist at that point:
    /// it is produced BY streaming the document. That is the whole reason
    /// <see cref="Sitemaps.SitemapWriter"/> hashes on the way past rather than re-reading
    /// afterwards — re-reading would mean downloading back what was just uploaded.
    ///
    /// Advisory. A sink that stores no metadata may ignore it; the transactional record of
    /// record is <c>seo_rebuild_log</c>.
    /// </summary>
    void RecordChecksum(string fileName, string checksum);

    /// <summary>
    /// Declares that a staged file is byte-identical to the one already live, so the sink
    /// may skip both uploading and promoting it (PLAN §7's conditional write).
    ///
    /// The caller must only say this when it has verified BOTH that the checksum matches its
    /// own transactional record AND that the live object really carries that checksum.
    /// Skipping on the first alone would leave a file permanently missing from a bucket that
    /// was restored from backup — the record would say "already published" forever, and the
    /// index would name a 404 that no rebuild ever repairs.
    /// </summary>
    void MarkUnchanged(string fileName);

    /// <summary>
    /// Publishes the staged files.
    ///
    /// Implementations must promote children BEFORE the index, and remove superseded files
    /// only AFTER it. That ordering is the reason a reader mid-swap is safe: the old index
    /// only ever references files that still exist, and the new index is only ever visible
    /// once everything it references already is.
    /// </summary>
    /// <param name="indexFileName">
    /// The file that must land last because everything else is reachable through it.
    /// </param>
    /// <param name="obsolete">
    /// Files that were live before and are not part of this set — a chunk lost when the
    /// corpus shrank below a 50k boundary, say. Deleted after the index no longer points at
    /// them; left alone otherwise, so a shrinking corpus does not 404 a crawler mid-crawl.
    /// </param>
    Task CommitAsync(
        string indexFileName,
        IReadOnlyCollection<string> obsolete,
        CancellationToken cancellationToken);
}
