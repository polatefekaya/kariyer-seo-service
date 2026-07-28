namespace Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;

/// <summary>
/// One incremental re-projection of <c>sitemap-jobs</c>.
///
/// A one-method interface between the debounce loop and the work it drives, so the
/// coalescing behaviour can be asserted on its own. That matters more than it looks: the
/// debounce is what turns a burst of a hundred expiries into ONE re-projection, and getting
/// it wrong does not produce a wrong sitemap — it produces a correct one, written a hundred
/// times, which shows up as an R2 bill months later and in no test or metric before that.
///
/// Testing it through the real projector would mean a Postgres container and a sink for every
/// timing assertion; testing it through this takes microseconds and a
/// <c>FakeTimeProvider</c>.
/// </summary>
public interface IDirtyFlusher
{
    /// <summary>Re-projects and swaps. Returns how many URLs the new file holds.</summary>
    Task<int> FlushAsync(CancellationToken cancellationToken);
}
