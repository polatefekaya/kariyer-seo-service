using Kariyer.Seo.Domain.Urls;

namespace Kariyer.Seo.Domain.Tests.Urls;

/// <summary>
/// The URLs this service publishes must match the SPA's canonical byte for byte.
///
/// A sitemap entry that differs by a slash is not "nearly right" — it is a second URL. Google
/// then has two addresses for one page, splits their signals between them, and picks a winner
/// itself. Nothing in any log here would show it.
/// </summary>
public sealed class UrlBuilderTests
{
    private const string Site = "https://kariyerzamani.com";

    [Fact]
    public void JobUrlMatchesTheCanonicalShape() =>
        Assert.Equal(
            "https://kariyerzamani.com/is-ilanlari/ilan/yazilim-muhendisi-istanbul-abc123",
            JobUrl.For(Site, "yazilim-muhendisi-istanbul-abc123"));

    [Fact]
    public void ATrailingSlashOnTheOriginIsAbsorbed() =>
        // The single most common way this value arrives wrong from an environment variable.
        // Tolerated rather than rejected, because the correct URL is unambiguous.
        Assert.Equal(
            JobUrl.For(Site, "slug"),
            JobUrl.For("https://kariyerzamani.com/", "slug"));

    [Fact]
    public void TheHomePageKeepsItsTrailingSlash() =>
        // The SPA canonicals the root as `https://kariyerzamani.com/`. Emitting the bare
        // origin would create a duplicate of the most important page on the site.
        Assert.Equal("https://kariyerzamani.com/", SiteUrls.Absolute(Site, "/"));

    [Fact]
    public void TheSlugIsUsedVerbatim()
    {
        // Not re-slugified, not lower-cased, not re-encoded. company_job.slug_url is what the
        // SPA routes on, so any transformation here produces a URL that looks correct and
        // 404s.
        const string slug = "Yazilim-Muhendisi-2026";

        Assert.Equal($"{Site}/is-ilanlari/ilan/{slug}", JobUrl.For(Site, slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySlugIsNotAddressable(string? slug) =>
        // Emitting it would produce `/is-ilanlari/ilan/`, which is not a page — one
        // guaranteed 404 in a file whose credibility is measured by how few it contains.
        Assert.False(JobUrl.IsAddressable(slug));

    [Fact]
    public void FacetUrlsAreMadeAbsoluteWithoutReshaping() =>
        Assert.Equal(
            "https://kariyerzamani.com/is-ilanlari/istanbul/yazilim",
            FacetUrl.For(Site, "/is-ilanlari/istanbul/yazilim"));

    [Theory]
    [InlineData("/is-ilanlari", true)]
    [InlineData("/is-ilanlari/istanbul", true)]
    [InlineData("/is-ilanlari/istanbul/yazilim", true)]
    [InlineData("/sirketler", false)]
    [InlineData("//evil.example/is-ilanlari", false)]
    [InlineData("https://evil.example/is-ilanlari", false)]
    [InlineData("/is-ilanlari/../admin", false)]
    [InlineData("/is-ilanlarimiz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ManifestPathsAreValidatedNotTrusted(string? path, bool expected) =>
        // The manifest is fetched over HTTP from another repository's build output. A bad
        // deploy there must not be able to put an absolute URL, a protocol-relative host, or
        // a traversal into the file Google reads as our statement about our own site.
        Assert.Equal(expected, FacetUrl.IsFacetPath(path));

    [Fact]
    public void PrerenderKeysCoverAllThreeUrlShapes()
    {
        string[] keys = PrerenderKeys.For(Site, "yazilim-muhendisi-1");

        // All three, not just the canonical. The two legacy paths still 301 here, and a bot
        // that followed one got its snapshot cached under the LEGACY key — so purging only
        // the canonical leaves a withdrawn job serving a fully rendered 'apply now' page,
        // from cache, for the whole TTL.
        Assert.Equal(
        [
            "prerender:https://kariyerzamani.com/is-ilanlari/ilan/yazilim-muhendisi-1",
            "prerender:https://kariyerzamani.com/ilanlar/slug/yazilim-muhendisi-1",
            "prerender:https://kariyerzamani.com/jobs/slug/yazilim-muhendisi-1",
        ], keys);
    }

    [Fact]
    public void PrerenderKeysAreUnaffectedByATrailingSlashOnTheOrigin() =>
        // The prerenderer keys on the exact URL string it was asked to render, so an extra
        // slash here produces three keys that match nothing and a purge that silently
        // removes zero snapshots.
        Assert.Equal(
            PrerenderKeys.For(Site, "slug"),
            PrerenderKeys.For("https://kariyerzamani.com/", "slug"));
}
