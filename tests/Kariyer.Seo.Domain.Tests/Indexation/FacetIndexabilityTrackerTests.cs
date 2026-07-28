using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Tests.Indexation;

/// <summary>
/// The diff that keeps <c>FacetIndexabilityChangedEvent</c> a transition event rather than a
/// state dump published every 45 minutes for ~3,000 facets.
/// </summary>
public sealed class FacetIndexabilityTrackerTests
{
    [Fact]
    public void ReportsBothDirections()
    {
        Dictionary<string, FacetIndexability> current = new(StringComparer.Ordinal)
        {
            ["/is-ilanlari/istanbul/yazilim"] = new(Indexable: true, JobCount: 12),
            ["/is-ilanlari/hakkari/yazilim"] = new(Indexable: false, JobCount: 2),
        };

        Dictionary<string, bool> previous = new(StringComparer.Ordinal)
        {
            ["/is-ilanlari/istanbul/yazilim"] = false,
            ["/is-ilanlari/hakkari/yazilim"] = true,
        };

        IReadOnlyList<FacetIndexabilityChange> changes =
            FacetIndexabilityTracker.Diff(current, previous);

        Assert.Equal(2, changes.Count);
        Assert.Equal("/is-ilanlari/hakkari/yazilim", changes[0].FacetPath);
        Assert.False(changes[0].Indexable);
        Assert.True(changes[1].Indexable);
    }

    [Fact]
    public void UnchangedFacetsProduceNothing()
    {
        // The reason the previous state is persisted at all. Without this, every rebuild
        // would republish the state of every candidate — a few million messages a month, of
        // which essentially none carry news, and "a facet became indexable" would stop being
        // something anyone could alert on.
        Dictionary<string, FacetIndexability> current = new(StringComparer.Ordinal)
        {
            ["/is-ilanlari/istanbul"] = new(true, 900),
            ["/is-ilanlari/hakkari"] = new(false, 1),
        };

        Dictionary<string, bool> previous = new(StringComparer.Ordinal)
        {
            ["/is-ilanlari/istanbul"] = true,
            ["/is-ilanlari/hakkari"] = false,
        };

        Assert.Empty(FacetIndexabilityTracker.Diff(current, previous));
    }

    [Fact]
    public void FirstSightReportsOnlyTheIndexableOnes()
    {
        // The very first rebuild sees ~3,000 unknown facets, of which most will never clear
        // their threshold. Announcing "this page you have never heard of is still not
        // indexable" 2,800 times is noise; the one that arrives already earning its place is
        // news.
        Dictionary<string, FacetIndexability> current = new(StringComparer.Ordinal)
        {
            ["/is-ilanlari/istanbul"] = new(true, 900),
            ["/is-ilanlari/hakkari"] = new(false, 1),
        };

        FacetIndexabilityChange change = Assert.Single(
            FacetIndexabilityTracker.Diff(current, new Dictionary<string, bool>()));

        Assert.Equal("/is-ilanlari/istanbul", change.FacetPath);
        Assert.True(change.Indexable);
        Assert.Equal(900, change.JobCount);
    }

    [Fact]
    public void ChangesAreOrderedByPath() =>
        // So a rebuild is reproducible and a test can assert on a sequence rather than on a
        // set that happens to enumerate in dictionary order.
        Assert.Equal(
            ["/is-ilanlari/a", "/is-ilanlari/b", "/is-ilanlari/c"],
            FacetIndexabilityTracker.Diff(
                new Dictionary<string, FacetIndexability>(StringComparer.Ordinal)
                {
                    ["/is-ilanlari/c"] = new(true, 10),
                    ["/is-ilanlari/a"] = new(true, 10),
                    ["/is-ilanlari/b"] = new(true, 10),
                },
                new Dictionary<string, bool>()).Select(c => c.FacetPath));
}
