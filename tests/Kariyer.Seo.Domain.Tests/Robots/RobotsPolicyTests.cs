using Kariyer.Seo.Domain.Robots;

namespace Kariyer.Seo.Domain.Tests.Robots;

public sealed class RobotsPolicyTests
{
    private const string Site = "https://kariyerzamani.com";

    [Fact]
    public void MatchesTheGoldenFile()
    {
        string robots = RobotsPolicy.Build(
            Site, "/sitemap.xml", ["/api/", "/hesabim", "/isveren/panel", "/admin"]);

        Fixtures.AssertMatches("robots-golden.txt", robots);
    }

    [Fact]
    public void TheSitemapReferenceIsAbsolute()
    {
        // robots.txt requires an absolute URL for Sitemap:, unlike every other directive in
        // it. A relative one is silently ignored, so the index would never be discovered by
        // any crawler that was not told about it through Search Console.
        string robots = RobotsPolicy.Build(Site, "/sitemap.xml", []);

        Assert.Contains("Sitemap: https://kariyerzamani.com/sitemap.xml", robots, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankDisallowEntriesAreDropped()
    {
        // A bare "Disallow:" means ALLOW EVERYTHING in the robots.txt grammar — the exact
        // opposite of what an empty config entry looks like it should mean. Emitting one
        // because a list had a stray empty string would silently neutralise the whole block.
        string robots = RobotsPolicy.Build(Site, "/sitemap.xml", ["/api/", "", "   "]);

        Assert.DoesNotContain("Disallow:\n", robots, StringComparison.Ordinal);
        Assert.Contains("Disallow: /api/", robots, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputIsDeterministic() =>
        Assert.Equal(
            RobotsPolicy.Build(Site, "/sitemap.xml", ["/api/"]),
            RobotsPolicy.Build(Site, "/sitemap.xml", ["/api/"]));

    [Fact]
    public void UsesUnixLineEndings()
    {
        // Written on whatever platform the builder runs on and read by parsers that are not
        // uniformly tolerant of CRLF. Pinning it also keeps the checksum stable across a
        // developer machine and the container.
        string robots = RobotsPolicy.Build(Site, "/sitemap.xml", ["/api/"]);

        Assert.DoesNotContain('\r', robots);
    }
}
