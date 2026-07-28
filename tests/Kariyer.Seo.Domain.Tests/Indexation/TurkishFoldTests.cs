using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Tests.Indexation;

/// <summary>
/// The fold has to agree with the web app's <c>foldSlug</c> exactly, because one side builds
/// the URL and the other matches database values against it.
/// </summary>
public sealed class TurkishFoldTests
{
    [Theory]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("Şanlıurfa", "sanliurfa")]
    [InlineData("Çanakkale", "canakkale")]
    [InlineData("Kırıkkale", "kirikkale")]
    [InlineData("Muğla", "mugla")]
    [InlineData("Düzce", "duzce")]
    [InlineData("Bilişim - İnternet", "bilisim-internet")]
    [InlineData("Yazılım Mühendisi", "yazilim-muhendisi")]
    [InlineData("UI/UX Tasarımcısı", "ui-ux-tasarimcisi")]
    [InlineData("Tam Zamanlı", "tam-zamanli")]
    [InlineData("Stajyer (Zorunlu)", "stajyer-zorunlu")]
    public void FoldsTurkishTextToTheSameSlugTheSpaBuilds(string input, string expected) =>
        Assert.Equal(expected, TurkishFold.Slug(input));

    [Fact]
    public void DottedAndDotlessIBothFoldToPlainI()
    {
        // The reason this class exists instead of ToLowerInvariant. Under
        // InvariantGlobalization, 'İ'.ToLowerInvariant() yields 'i' plus a COMBINING DOT
        // ABOVE — a two-char sequence that never equals the 'i' in a URL slug. İstanbul would
        // stop matching istanbul, and the largest city in the country would silently drop out
        // of the filter sitemap.
        Assert.Equal("i", TurkishFold.Slug("İ"));
        Assert.Equal("i", TurkishFold.Slug("I"));
        Assert.Equal("i", TurkishFold.Slug("ı"));
        Assert.Equal("i", TurkishFold.Slug("i"));

        Assert.NotEqual("i", "İ".ToLowerInvariant());
    }

    [Theory]
    [InlineData("  Bilişim  ", "bilisim")]
    [InlineData("---a---b---", "a-b")]
    [InlineData("!!!", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SeparatorRunsCollapseAndTrim(string? input, string expected) =>
        Assert.Equal(expected, TurkishFold.Slug(input));

    [Fact]
    public void ContainsMirrorsTheApiSubstringMatch()
    {
        // The jobs API matches department and position with iLike '%value%'. That is why the
        // sector value 'Bilişim' legitimately selects rows whose department is
        // 'Bilişim - İnternet'. Using equality instead would undercount exactly the curated
        // sector pages the manifest exists to serve.
        Assert.True(TurkishFold.Contains("Bilişim - İnternet", "Bilişim"));
        Assert.True(TurkishFold.Contains("bilisim internet", "BİLİŞİM"));
        Assert.False(TurkishFold.Contains("Finans", "Bilişim"));
    }

    [Fact]
    public void AnEmptyNeedleNeverMatches()
    {
        // Under a naive substring test "" matches everything, which would turn one malformed
        // manifest value into a facet that claims the entire corpus.
        Assert.False(TurkishFold.Contains("anything", ""));
        Assert.False(TurkishFold.Contains("anything", null));
        Assert.False(TurkishFold.Contains("anything", "   "));
    }

    [Fact]
    public void EqualIsExactOverFoldedForms()
    {
        Assert.True(TurkishFold.Equal("İstanbul", "istanbul"));
        Assert.False(TurkishFold.Equal("İstanbul", "istanbul-anadolu"));
        Assert.False(TurkishFold.Equal(null, "istanbul"));
        Assert.False(TurkishFold.Equal("", ""));
    }
}
