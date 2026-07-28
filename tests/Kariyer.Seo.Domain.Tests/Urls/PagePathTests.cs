using Kariyer.Seo.Domain.Urls;

namespace Kariyer.Seo.Domain.Tests.Urls;

/// <summary>
/// CMS page paths come from a table another service owns. This is the boundary check that
/// stops a bad row becoming a URL in the document Google reads as our statement about our own
/// site.
/// </summary>
public sealed class PagePathTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/kariyer-rehberi")]
    [InlineData("/kariyer-rehberi/cv-nasil-yazilir")]
    [InlineData("/rehber/is-gorusmesi-ipuclari")]
    public void AcceptsOrdinarySiteRelativePaths(string path) =>
        Assert.True(PagePath.IsPublishable(path));

    [Theory]
    [InlineData("https://evil.example/x")]        // absolute URL in a relative-path column
    [InlineData("//evil.example/x")]              // protocol-relative — an absolute URL once resolved
    [InlineData("/kariyer-rehberi/../admin")]     // traversal
    [InlineData("kariyer-rehberi/x")]             // not rooted
    [InlineData("/path with space")]              // encodes inconsistently → two URLs for one page
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingThatWouldNotAddressOurOwnSite(string? path) =>
        Assert.False(PagePath.IsPublishable(path));

    [Fact]
    public void RejectsControlCharacters() =>
        // A newline in a <loc> would split the element and produce a document that either
        // fails to parse or silently drops the rest of the file.
        Assert.False(PagePath.IsPublishable("/kariyer-rehberi/a\nb"));

    [Fact]
    public void DoesNotReshapeAnything()
    {
        // The contract is skip-or-accept, never "fix". Rewriting a path here would produce a
        // URL the CMS's own resolver would not serve — worse than omitting the page, because
        // it advertises a 404 instead of nothing.
        Assert.True(PagePath.IsPublishable("/Kariyer-Rehberi/CV"));
        Assert.True(PagePath.IsPublishable("/kariyer-rehberi/x/"));
    }
}
