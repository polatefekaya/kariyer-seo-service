using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Kariyer.Seo.Worker.Common.Caching;

/// <summary>Wires the prerender cache, or an explicit no-op when it is switched off.</summary>
public static class CachingExtensions
{
    public static IServiceCollection AddPrerenderCache(
        this IServiceCollection services, IConfiguration configuration, bool needed)
    {
        GarnetOptions garnet = new();
        configuration.GetSection(GarnetOptions.SectionName).Bind(garnet);

        if (!needed || !garnet.Enabled)
        {
            services.AddSingleton<IPrerenderCache, DisabledPrerenderCache>();
            return services;
        }

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            string connection = sp.GetRequiredService<IOptions<GarnetOptions>>().Value.ConnectionString;

            ConfigurationOptions config = ConfigurationOptions.Parse(connection);

            // Boot must not depend on Garnet being up. A pod that refused to start because a
            // cache was restarting would stop consuming freshness events entirely — trading a
            // stale prerendered page for a sitemap that no longer updates at all, which is
            // the strictly worse failure.
            config.AbortOnConnectFail = false;

            // Bounded so a purge cannot hold a consumer open indefinitely against an
            // unreachable cache; the operation is retried on the next delivery anyway.
            config.ConnectTimeout = 5_000;
            config.SyncTimeout = 5_000;

            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<IPrerenderCache, GarnetPrerenderCache>();

        return services;
    }
}
