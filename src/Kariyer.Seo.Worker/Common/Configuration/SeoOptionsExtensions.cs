using Kariyer.Seo.Worker.Common.Roles;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Common.Configuration;

/// <summary>
/// Binds and validates every configuration section at startup.
///
/// All of it uses <c>ValidateOnStart</c> deliberately, and the extra rules below all guard
/// the same class of failure: a misconfiguration this service cannot notice at runtime.
///
/// This service's output is consumed by a crawler, not by a user. Nothing pages when the
/// sitemap is wrong — there is no 500, no failed request, no angry customer within the hour.
/// A blank R2 bucket, a site URL with the wrong scheme, or a staging prefix that Cloudflare
/// happens to serve produces a service that reports perfect health for weeks while the
/// index quietly decays. Startup is the only place those are cheap to catch.
/// </summary>
public static class SeoOptionsExtensions
{
    public static IServiceCollection AddSeoOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        RolePlan plan)
    {
        services.BindValidated<PersistenceOptions>(configuration, PersistenceOptions.SectionName);
        services.BindValidated<RabbitOptions>(configuration, RabbitOptions.SectionName);
        services.BindValidated<EventsOptions>(configuration, EventsOptions.SectionName);

        services.BindValidated<SeoOptions>(configuration, SeoOptions.SectionName)
            .Validate(
                o => Uri.TryCreate(o.SiteUrl, UriKind.Absolute, out Uri? site)
                     && site.Scheme == Uri.UriSchemeHttps
                     && string.IsNullOrEmpty(site.PathAndQuery.TrimEnd('/')),
                "Seo:SiteUrl must be an absolute https origin with no path, e.g. "
                + "'https://kariyerzamani.com'. Every URL in every sitemap is built from it, so a "
                + "wrong scheme or a stray path segment does not fail — it publishes a few hundred "
                + "thousand URLs that all redirect, which burns crawl budget and tells Google the "
                + "file is stale.")
            .Validate(
                o => o.Thresholds.SingleAxis > 0 && o.Thresholds.Combo > 0,
                "Seo:Thresholds must both be greater than zero. A threshold of zero admits every "
                + "candidate in the facet manifest — several thousand paths, most with no jobs at "
                + "all — turning sitemap-jobfilters.xml into a doorway-page farm, which is the one "
                + "thing Google penalises a jobs site for by hand.")
            .Validate(
                o => o.Thresholds.Combo >= o.Thresholds.SingleAxis,
                "Seo:Thresholds:Combo must be >= SingleAxis. A combo page is thinner and more "
                + "doorway-like than either axis alone, so it cannot require LESS evidence to be "
                + "worth indexing.")
            .Validate(
                o => o.DebounceWindow > TimeSpan.Zero && o.DebounceWindow < o.CronInterval,
                "Seo:DebounceWindow must be positive and shorter than Seo:CronInterval. Zero "
                + "re-projects sitemap-jobs once per expiry instead of once per burst; longer than "
                + "the cron makes the incremental path pointless because the full rebuild always "
                + "wins the race.")
            .Validate(
                o => o.R2.StagingPrefix.Length > 0
                     && !o.R2.StagingPrefix.StartsWith("sitemap", StringComparison.OrdinalIgnoreCase),
                "Seo:R2:StagingPrefix must not begin with 'sitemap'. The Cloudflare rule maps "
                + "/sitemap*.xml to this bucket, so a staging key matching that pattern would serve "
                + "half-written files to Googlebot — defeating the entire point of staging.")
            .Validate(
                o => o.StaticPaths.All(p => p.StartsWith('/')),
                "Every Seo:StaticPaths entry must be site-relative and start with '/'. An absolute "
                + "URL here would be concatenated onto the origin and emitted as nonsense.");

        // R2 credentials are only required of a role that actually writes. A future
        // read-only role should be able to boot without them rather than being handed the
        // keys to a bucket it never touches.
        if (plan.WritesToR2)
        {
            services.AddOptions<SeoOptions>().Validate(
                o => o.R2.IsConfigured,
                "This role writes sitemaps to R2, but Seo:R2 is incomplete (Endpoint, Bucket, "
                + "AccessKey and SecretKey are all required). Nothing at runtime would report this: "
                + "the rebuild would simply fail every 45 minutes into a log line, while /health and "
                + "/health/ready both stayed green and the live sitemap silently aged.");
        }

        if (plan.NeedsFacetManifest)
        {
            services.BindValidated<FacetManifestOptions>(
                configuration, $"{SeoOptions.SectionName}:{nameof(SeoOptions.FacetManifest)}");
        }

        services.BindValidated<GarnetOptions>(configuration, GarnetOptions.SectionName)
            .Validate(
                o => !(o.Enabled && plan.NeedsPrerenderCache) || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Garnet:Enabled is true on a role that purges the prerender cache, but "
                + "Garnet:ConnectionString is empty. A purge that never happens is invisible: the "
                + "job leaves the sitemap on schedule while the prerenderer keeps serving a fully "
                + "rendered 'apply now' page for a withdrawn posting until the TTL expires. Set the "
                + "connection string, or set Enabled=false and mean it.");

        services.BindValidated<IndexingOptions>(configuration, IndexingOptions.SectionName)
            .Validate(
                o => !o.Enabled || File.Exists(o.CredentialsPath),
                "Indexing:Enabled is true but Indexing:CredentialsPath does not point at a readable "
                + "file. The submitter degrades to a logged failure on every call and never throws, "
                + "so a missing key is indistinguishable at runtime from the feature being switched "
                + "off — startup is the only place to tell them apart.");

        return services;
    }

    private static OptionsBuilder<T> BindValidated<T>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where T : class =>
        services.AddOptions<T>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
}
