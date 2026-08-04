using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Roles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Tests.Configuration;

/// <summary>
/// Startup validation.
///
/// Every rule here guards a misconfiguration this service cannot notice at runtime. Its
/// output is read by a crawler, not a user: a blank bucket, a site URL with the wrong scheme,
/// or a staging prefix Cloudflare happens to serve produces a service that reports perfect
/// health for weeks while the index quietly decays. Startup is the only place these are cheap
/// to catch, so these tests are what make "enabled means usable" a real property.
/// </summary>
public sealed class SeoOptionsTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Seo:SiteUrl"] = "https://kariyerzamani.com",
        ["Seo:CronInterval"] = "00:45:00",
        ["Seo:DebounceWindow"] = "00:00:30",
        ["Seo:Thresholds:SingleAxis"] = "5",
        ["Seo:Thresholds:Combo"] = "10",
        ["Seo:CacheControl"] = "public, max-age=600",
        ["Seo:R2:Endpoint"] = "https://account.r2.cloudflarestorage.com",
        ["Seo:R2:Bucket"] = "kariyer-seo",
        ["Seo:R2:AccessKey"] = "key",
        ["Seo:R2:SecretKey"] = "secret",
        ["Seo:R2:StagingPrefix"] = "_staging/",
        ["Seo:FacetManifest:Url"] = "https://kariyerzamani.com/seo/facet-manifest.json",
        ["Garnet:Enabled"] = "false",

        // Spelled out here because the options class no longer carries them. A non-empty
        // collection initialiser is what doubled these lists against appsettings.json — the
        // binder appends to a populated collection instead of replacing it — so the defaults
        // are gone and configuration is the only source. Every test below inherits these two.
        ["Seo:StaticPaths:0"] = "/",
        ["Seo:StaticPaths:1"] = "/hakkimizda",
        ["Seo:DisallowedPaths:0"] = "/api/",
        ["Seo:DisallowedPaths:1"] = "/cms-preview",
    };

    [Fact]
    public void TheShippedDefaultsValidate() => Assert.NotNull(Resolve(Valid).SiteUrl);

    [Theory]
    [InlineData("http://kariyerzamani.com")]
    [InlineData("https://kariyerzamani.com/tr")]
    [InlineData("kariyerzamani.com")]
    [InlineData("")]
    public void ABadSiteUrlFailsStartup(string siteUrl)
    {
        // Every URL in every sitemap is built from this. A wrong scheme or a stray path
        // segment does not throw at runtime — it publishes several hundred thousand URLs that
        // all redirect, burning crawl budget and telling Google the file is stale.
        Assert.Throws<OptionsValidationException>(() => Resolve(With("Seo:SiteUrl", siteUrl)));
    }

    [Fact]
    public void ATrailingSlashOnTheSiteUrlIsAccepted() =>
        // Tolerated, because SiteUrls.Absolute normalises it and it is the most common way
        // the value arrives from an environment variable.
        Assert.NotNull(Resolve(With("Seo:SiteUrl", "https://kariyerzamani.com/")));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void AZeroThresholdFailsStartup(string value) =>
        // Zero admits every candidate in the manifest — several thousand paths, most with no
        // jobs at all — turning sitemap-jobfilters.xml into a doorway-page farm, which is the
        // one thing Google penalises a jobs site for by hand.
        Assert.Throws<OptionsValidationException>(
            () => Resolve(With("Seo:Thresholds:SingleAxis", value)));

    [Fact]
    public void AComboThresholdBelowSingleAxisFailsStartup() =>
        // A combo page is thinner and more doorway-like than either axis alone, so it cannot
        // require LESS evidence to be worth indexing.
        Assert.Throws<OptionsValidationException>(
            () => Resolve(With("Seo:Thresholds:Combo", "3")));

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("01:00:00")]
    public void ABadDebounceWindowFailsStartup(string window) =>
        // Zero re-projects sitemap-jobs once per expiry instead of once per burst; longer
        // than the cron makes the incremental path pointless because the full rebuild always
        // wins the race.
        Assert.Throws<OptionsValidationException>(
            () => Resolve(With("Seo:DebounceWindow", window)));

    [Fact]
    public void AStagingPrefixThatCloudflareWouldServeFailsStartup() =>
        // The Cloudflare rule maps /sitemap*.xml to this bucket, so a staging key matching
        // that pattern would serve half-written files to Googlebot — defeating the entire
        // point of staging.
        Assert.Throws<OptionsValidationException>(
            () => Resolve(With("Seo:R2:StagingPrefix", "sitemap-staging/")));

    [Fact]
    public void AnIncompleteBucketFailsStartupForAnR2Writer()
    {
        Dictionary<string, string?> config = With("Seo:R2:Bucket", "");

        // Nothing at runtime would report this: the rebuild would fail every 45 minutes into
        // a log line while /health and /health/ready both stayed green and the live sitemap
        // silently aged.
        Assert.Throws<OptionsValidationException>(() => Resolve(config, ServiceRole.Builder));
    }

    [Fact]
    public void GarnetEnabledWithNoConnectionStringFailsStartupOnAConsumer()
    {
        Dictionary<string, string?> config = new(Valid) { ["Garnet:Enabled"] = "true" };

        // A purge that never happens is invisible: the job leaves the sitemap on schedule
        // while the prerenderer keeps serving its rendered 'apply now' page until the TTL.
        Assert.Throws<OptionsValidationException>(() => Resolve(config, ServiceRole.Reactor));
    }

    [Fact]
    public void GarnetIsNotRequiredOfARoleThatNeverPurges()
    {
        Dictionary<string, string?> config = new(Valid) { ["Garnet:Enabled"] = "true" };

        // The builder consumes no freshness events, so it never purges — handing it a Garnet
        // connection it will not use would be one more dependency that can fail a boot.
        Assert.NotNull(Resolve(config, ServiceRole.Builder));
    }

    [Fact]
    public void IndexingEnabledWithoutAReadableKeyFailsStartup()
    {
        Dictionary<string, string?> config = new(Valid)
        {
            ["Indexing:Enabled"] = "true",
            ["Indexing:CredentialsPath"] = "/nonexistent/key.json",
        };

        // The submitter degrades to a logged failure on every call and never throws, so a
        // missing key is indistinguishable at runtime from the feature being switched off.
        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void AStaticPathThatIsNotSiteRelativeFailsStartup()
    {
        Dictionary<string, string?> config = new(Valid)
        {
            ["Seo:StaticPaths:0"] = "https://kariyerzamani.com/hakkimizda",
        };

        // An absolute URL here would be concatenated onto the origin and emitted as nonsense.
        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void ADisallowedPathThatIsNotSiteRelativeFailsStartup()
    {
        Dictionary<string, string?> config = new(Valid)
        {
            ["Seo:DisallowedPaths:0"] = "https://kariyerzamani.com/api/",
        };

        // A Disallow rule is matched against the path alone, so an absolute URL never matches
        // anything. The line looks like protection and provides none.
        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void ADuplicateStaticPathFailsStartup()
    {
        // THE REGRESSION, in the shape configuration can still produce it.
        //
        // It reached production through the options class rather than through this file: both
        // lists had non-empty C# initialisers, and the configuration binder APPENDS bound
        // values to a pre-populated collection instead of replacing it, so the defaults and
        // the identical appsettings.json entries both survived. The live bucket served a
        // sitemap-static-1.xml.gz with 14 <url> entries for 7 pages, and duplicate <loc> is
        // invalid per the sitemaps protocol.
        //
        // The initialisers are empty now, which closes that door. This closes the other one:
        // a duplicate typed into appsettings.json, or an environment variable landing on an
        // index the file already uses.
        Dictionary<string, string?> config = new(Valid) { ["Seo:StaticPaths:2"] = "/" };

        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void ADuplicateDisallowedPathFailsStartup()
    {
        // Same defect, the other list — it emitted every Disallow line in robots.txt twice.
        Dictionary<string, string?> config = new(Valid) { ["Seo:DisallowedPaths:2"] = "/api/" };

        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void ADuplicateIsRejectedRatherThanDeduplicated()
    {
        Dictionary<string, string?> config = new(Valid) { ["Seo:StaticPaths:2"] = "/" };

        OptionsValidationException error =
            Assert.Throws<OptionsValidationException>(() => Resolve(config));

        // Pinned because the alternative — collapsing the list quietly — is the tempting fix
        // and the wrong one. It would publish a correct sitemap while leaving configuration
        // bound in a way its author does not believe, so the NEXT thing that breaks arrives
        // with no history. The message has to name the cause, because nobody guesses "the
        // binder appends to a pre-populated collection" from a duplicated URL.
        string message = string.Join(' ', error.Failures);

        Assert.Contains("Seo:StaticPaths", message, StringComparison.Ordinal);
        Assert.Contains("APPENDS", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStaticPathListFailsStartup()
    {
        // With the C# defaults gone, an empty list means configuration did not supply one —
        // and nothing downstream would report it. The rebuild still writes
        // sitemap-static-1.xml, still uploads it, still succeeds; the file is a valid, empty
        // <urlset>, and the home page simply stops being advertised.
        Dictionary<string, string?> config = new(Valid);
        config.Remove("Seo:StaticPaths:0");
        config.Remove("Seo:StaticPaths:1");

        Assert.Throws<OptionsValidationException>(() => Resolve(config));
    }

    [Fact]
    public void AnEmptyDisallowedPathListIsAccepted()
    {
        // The deliberate asymmetry with the test above: `Allow: /` and nothing else is a
        // coherent robots.txt that merely spends crawl budget, not a silent de-listing. A
        // deployment could mean it.
        Dictionary<string, string?> config = new(Valid);
        config.Remove("Seo:DisallowedPaths:0");
        config.Remove("Seo:DisallowedPaths:1");

        Assert.Empty(Resolve(config).DisallowedPaths);
    }

    private static Dictionary<string, string?> With(string key, string? value) =>
        new(Valid) { [key] = value };

    private static SeoOptions Resolve(
        Dictionary<string, string?> settings, ServiceRole role = ServiceRole.All)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = [];
        services.AddSeoOptions(configuration, RolePlan.For(role));

        using ServiceProvider provider = services.BuildServiceProvider();

        // ValidateOnStart runs through IStartupValidator; resolving the value forces the same
        // validators, which is what the host would do at boot.
        provider.GetRequiredService<IStartupValidator>().Validate();

        return provider.GetRequiredService<IOptions<SeoOptions>>().Value;
    }
}
