namespace Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;

/// <summary>
/// Notifies Google that a job URL appeared or disappeared (PLAN §4, optional).
///
/// Why it is worth having at all, given the sitemap already carries <c>&lt;lastmod&gt;</c>:
/// Google retired the sitemap ping endpoint, so a sitemap change is only noticed on the next
/// crawl of the file — hours, sometimes days. For a withdrawn job that is hours of search
/// results pointing at a dead posting. The Indexing API is the only push channel Google
/// offers, and <c>JobPosting</c> is one of the two content types it officially supports.
///
/// An interface with a no-op implementation, rather than a nullable dependency, so the
/// consumers have no <c>if (indexing is not null)</c> branch and the "off" path is a real
/// object that can be asserted on.
/// </summary>
public interface IIndexingSubmitter
{
    /// <summary>
    /// Submits one URL.
    ///
    /// Must never throw. It is called after the state change has already committed, so a
    /// failure here has to degrade to a logged, counted miss — faulting the message would
    /// replay a committed removal to retry a best-effort notification.
    /// </summary>
    Task SubmitAsync(string jobUid, string slug, IndexingAction action, CancellationToken ct);
}

/// <summary>What happened to the URL, in Google's terms.</summary>
public enum IndexingAction
{
    /// <summary>The page exists or changed — <c>URL_UPDATED</c>.</summary>
    Updated,

    /// <summary>The page is gone — <c>URL_DELETED</c>.</summary>
    Deleted,
}

/// <summary>
/// The implementation used when the feature is off, which is the default.
///
/// It logs at debug rather than warning, unlike <c>DisabledPrerenderCache</c>. The difference
/// is deliberate: a missed Garnet purge serves a wrong page to a real person, whereas a
/// missed Indexing API call only means Google finds out on its own schedule from the sitemap.
/// One is a defect, the other is the documented baseline.
/// </summary>
public sealed class DisabledIndexingSubmitter(ILogger<DisabledIndexingSubmitter> logger)
    : IIndexingSubmitter
{
    public Task SubmitAsync(string jobUid, string slug, IndexingAction action, CancellationToken ct)
    {
        logger.LogDebug(
            "Google Indexing API is disabled; {Action} for {JobUid} not submitted. The sitemap "
            + "lastmod remains the signal.", action, jobUid);

        return Task.CompletedTask;
    }
}
