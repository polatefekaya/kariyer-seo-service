namespace Kariyer.Seo.Domain.Ports;

/// <summary>
/// The prerendered-HTML cache (Garnet). Implemented in the Worker over RESP.
///
/// Only purging is exposed. This service never reads or writes a snapshot — the prerenderer
/// owns that — and a purge is the one operation that is safe to repeat: <c>DEL</c> on a
/// missing key is a no-op, which is what lets a consumer crash between its database commit
/// and its purge and simply repeat the purge on redelivery. See PLAN §6.1.
/// </summary>
public interface IPrerenderCache
{
    /// <summary>
    /// Removes every cached snapshot for a job slug — canonical and both legacy URL shapes.
    /// See <see cref="Urls.PrerenderKeys"/> for why all three matter.
    /// </summary>
    /// <returns>Keys that actually existed and were removed, for the metric.</returns>
    Task<int> PurgeJobAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the cached snapshot for one site path — a CMS landing page.
    ///
    /// Needed on publish as well as unpublish. An editor who fixes a typo and republishes has
    /// changed what the page says, but the prerenderer is still holding the previous render
    /// and will serve it to crawlers for the rest of its TTL. Without this the CMS's whole
    /// promise — "edit and it is live" — is true for browsers and false for Google.
    /// </summary>
    Task<int> PurgePathAsync(string path, CancellationToken cancellationToken);
}
