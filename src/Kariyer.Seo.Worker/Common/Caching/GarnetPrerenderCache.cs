using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Telemetry;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Kariyer.Seo.Worker.Common.Caching;

/// <summary>
/// Purges prerendered HTML from Garnet over RESP.
///
/// The failure this exists to prevent is the ugliest one in the system. A job is withdrawn;
/// this service drops it from the sitemap on schedule; and the prerenderer goes on serving a
/// fully rendered "apply now" page — to Googlebot and to every real visitor arriving from a
/// search result — for the whole six-hour TTL. Nothing is broken, nothing errors, and the
/// only party who could notice is the candidate applying for a job that no longer exists.
///
/// So purge failures are counted and logged, never swallowed silently, and never allowed to
/// fail the consumer: the database row is already committed and IS the truth, the purge is
/// idempotent, and the next flush or redelivery repeats it. Throwing here would roll back a
/// correct removal because a cache was briefly unreachable.
/// </summary>
public sealed class GarnetPrerenderCache(
    IConnectionMultiplexer multiplexer,
    IOptions<SeoOptions> seo,
    ILogger<GarnetPrerenderCache> logger) : IPrerenderCache
{
    public Task<int> PurgeJobAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            // A job with no slug was never addressable, so nothing can be cached for it.
            return Task.FromResult(0);
        }

        return PurgeAsync(PrerenderKeys.For(seo.Value.SiteUrl, slug), slug);
    }

    public Task<int> PurgePathAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(0);
        }

        return PurgeAsync([PrerenderKeys.ForPath(seo.Value.SiteUrl, path)], path);
    }

    private async Task<int> PurgeAsync(string[] keys, string subject)
    {
        try
        {
            IDatabase database = multiplexer.GetDatabase();

            // One round trip regardless of how many keys. KeyDeleteAsync(RedisKey[]) issues a
            // single DEL, which for a job's three URL shapes also makes the purge atomic with
            // respect to a concurrent render: either every snapshot for it is gone or none
            // are, never the canonical one alone.
            long removed = await database.KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);

            DiagnosticsConfig.PrerenderPurges.Add(removed,
                new KeyValuePair<string, object?>("outcome", "removed"));

            logger.LogDebug(
                "Purged {Removed} of {Total} prerender key(s) for {Subject}.",
                removed, keys.Length, subject);

            return (int)removed;
        }
        catch (RedisException ex)
        {
            DiagnosticsConfig.PrerenderPurgeFailures.Add(1);

            // Warning, not error, and deliberately not rethrown. The removal is already
            // durable in Postgres; this is a cache we could not reach. The TTL is the
            // backstop and the next redelivery retries.
            logger.LogWarning(ex,
                "Could not purge prerender keys for {Subject}. The change is already durable in "
                + "the database; the purge will be retried and the cache TTL is the backstop.",
                subject);

            return 0;
        }
    }
}

/// <summary>
/// Stands in when Garnet is deliberately switched off (<c>Garnet:Enabled=false</c>).
///
/// It exists so "purging is off" is a visible, explicit configuration rather than a null
/// reference or a silently skipped call — and so the log line says so on every expiry, which
/// is what stops a temporary local override from surviving into an environment where
/// withdrawn jobs then serve from cache for six hours at a time.
/// </summary>
public sealed class DisabledPrerenderCache(ILogger<DisabledPrerenderCache> logger) : IPrerenderCache
{
    public Task<int> PurgeJobAsync(string slug, CancellationToken cancellationToken) =>
        Warn(slug);

    public Task<int> PurgePathAsync(string path, CancellationToken cancellationToken) =>
        Warn(path);

    private Task<int> Warn(string subject)
    {
        logger.LogWarning(
            "Prerender purging is DISABLED; the cached page for {Subject} will survive until "
            + "its TTL expires.", subject);

        return Task.FromResult(0);
    }
}
