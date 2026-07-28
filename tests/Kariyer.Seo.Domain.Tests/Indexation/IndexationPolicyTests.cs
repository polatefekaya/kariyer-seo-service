using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Tests.Indexation;

/// <summary>
/// The count gate, asserted at the boundaries.
///
/// These thresholds decide which of ~3,000 candidate pages Google is told about. Off by one
/// in one direction publishes thin doorway pages — the thing a jobs site gets manually
/// penalised for; off by one in the other quietly withholds money pages that had earned
/// their place. Neither is visible in any log.
/// </summary>
public sealed class IndexationPolicyTests
{
    private static readonly IndexationThresholds Default = IndexationThresholds.Default;

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(50, true)]
    public void SingleAxisNeedsFive(int count, bool expected) =>
        Assert.Equal(expected, IndexationPolicy.IsIndexable(axes: 1, count, Default));

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(500, true)]
    public void ComboNeedsTen(int count, bool expected) =>
        Assert.Equal(expected, IndexationPolicy.IsIndexable(axes: 2, count, Default));

    [Fact]
    public void ThreeAxesUseTheComboThreshold()
    {
        // "Combo" is >= 2, not exactly 2. A city + work-type + role page is thinner still,
        // so it cannot be allowed to fall back to the single-axis bar.
        Assert.False(IndexationPolicy.IsIndexable(axes: 3, 9, Default));
        Assert.True(IndexationPolicy.IsIndexable(axes: 3, 10, Default));
    }

    [Fact]
    public void ThresholdsMatchTheSpaConstants()
    {
        // These mirror MIN_JOBS_SINGLE / MIN_JOBS_COMBO in the web app's
        // src/seo/facets/indexation.ts. If they drift, the sitemap advertises pages whose own
        // robots meta tag says noindex — a contradiction Google resolves by trusting the tag
        // and distrusting the whole sitemap.
        Assert.Equal(5, Default.SingleAxis);
        Assert.Equal(10, Default.Combo);
    }

    [Fact]
    public void AFacetThatConstrainsNothingIsNeverIndexable()
    {
        FacetDefinition unconstrained = new("/is-ilanlari", 1, null, [], [], [], []);

        // Such an entry matches every live job, so its count is the whole corpus and it would
        // always clear the threshold — landing in the sitemap as a "filter" that filters
        // nothing. Almost always a manifest generated before its registries loaded.
        Assert.False(IndexationPolicy.IsIndexable(unconstrained, 100_000, Default));
    }

    [Fact]
    public void ZeroAxesIsTreatedAsSingleAxis()
    {
        // Defensive: a malformed manifest entry with axes=0 must not get a threshold of zero
        // by falling through some arithmetic path. The stricter of the two readings wins.
        Assert.Equal(Default.SingleAxis, IndexationPolicy.MinimumFor(0, Default));
    }

    [Fact]
    public void UnusableThresholdsAreRecognised()
    {
        Assert.True(Default.IsUsable);
        Assert.False(new IndexationThresholds(0, 10).IsUsable);
        Assert.False(new IndexationThresholds(5, 0).IsUsable);
        Assert.False(new IndexationThresholds(-1, -1).IsUsable);
    }
}
