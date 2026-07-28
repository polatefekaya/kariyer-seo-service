using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Tests.Indexation;

/// <summary>
/// The fold that turns one database aggregate into ~3,000 facet counts.
///
/// The case that matters most is <see cref="SectorSlugsAreMatchedByValueNotBySlug"/>: it is
/// the concrete reason the manifest carries axis VALUES rather than only paths, and the
/// reason a slug-matching implementation would silently drop the highest-value pages on the
/// site while looking perfectly healthy.
/// </summary>
public sealed class FacetCountProjectorTests
{
    private static LiveJobFacetTuple Tuple(
        string? province = null,
        string? department = null,
        string? position = null,
        string[]? workingTypes = null,
        string[]? workingPrefs = null,
        int count = 1) =>
        new(province, department, position, workingTypes ?? [], workingPrefs ?? [], count);

    private static FacetDefinition Facet(
        string path,
        int axes,
        string? province = null,
        string[]? departments = null,
        string[]? positions = null,
        string[]? workingTypes = null,
        string[]? workingPrefs = null) =>
        new(path, axes, province, departments ?? [], positions ?? [], workingTypes ?? [],
            workingPrefs ?? []);

    [Fact]
    public void SectorSlugsAreMatchedByValueNotBySlug()
    {
        // /is-ilanlari/istanbul/yazilim maps, in the web app's SECTORS registry, to the
        // department values ["Bilişim", "Bilişim - İnternet"] — which share not one character
        // with the slug 'yazilim'. A service that folded the slug and matched it against the
        // column would count ZERO here, gate the facet out of the sitemap, and disagree
        // permanently with the SPA that goes on serving the page as index,follow.
        FacetDefinition facet = Facet(
            "/is-ilanlari/istanbul/yazilim", 2,
            province: "İstanbul",
            departments: ["Bilişim", "Bilişim - İnternet"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(province: "İstanbul", department: "Bilişim", count: 7),
            Tuple(province: "İstanbul", department: "Bilişim - İnternet", count: 5),
            Tuple(province: "İstanbul", department: "Finans", count: 40),
            Tuple(province: "Ankara", department: "Bilişim", count: 30),
        ];

        IReadOnlyDictionary<string, int> counts = FacetCountProjector.Project([facet], tuples);

        Assert.Equal(12, counts["/is-ilanlari/istanbul/yazilim"]);
    }

    [Fact]
    public void ComboFacetsAndTheAxesTogether()
    {
        FacetDefinition facet = Facet(
            "/is-ilanlari/istanbul/backend-developer", 2,
            province: "İstanbul",
            positions: ["Backend Developer", "Back End Developer"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(province: "İstanbul", position: "Backend Developer", count: 4),
            Tuple(province: "İstanbul", position: "Back End Developer", count: 3),

            // Right role, wrong city — must not count. This is the case a per-axis aggregate
            // gets wrong: count(city) × count(role) is not count(city AND role).
            Tuple(province: "Ankara", position: "Backend Developer", count: 100),

            // Right city, wrong role.
            Tuple(province: "İstanbul", position: "Grafik Tasarımcı", count: 50),
        ];

        Assert.Equal(7, FacetCountProjector.Project([facet], tuples)
            ["/is-ilanlari/istanbul/backend-developer"]);
    }

    [Fact]
    public void ArrayAxesMatchAnyElement()
    {
        // working_type is a text[] column, and the API expands a filter to
        // EXISTS (SELECT 1 FROM unnest(working_type) e WHERE e = 'Uzaktan').
        FacetDefinition facet = Facet(
            "/is-ilanlari/uzaktan", 1, workingTypes: ["Uzaktan"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(workingTypes: ["Uzaktan"], count: 3),
            Tuple(workingTypes: ["Ofisten", "Uzaktan"], count: 2),
            Tuple(workingTypes: ["Ofisten"], count: 90),
            Tuple(workingTypes: [], count: 40),
        ];

        Assert.Equal(5, FacetCountProjector.Project([facet], tuples)["/is-ilanlari/uzaktan"]);
    }

    [Fact]
    public void WorkTypeAndRoleAreAnded()
    {
        // The reserved work-type prefix case: /is-ilanlari/uzaktan-yazilim-muhendisi.
        FacetDefinition facet = Facet(
            "/is-ilanlari/uzaktan-yazilim-muhendisi", 2,
            positions: ["Yazılım Mühendisi"],
            workingTypes: ["Uzaktan"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(position: "Yazılım Mühendisi", workingTypes: ["Uzaktan"], count: 6),
            Tuple(position: "Yazılım Mühendisi", workingTypes: ["Ofisten"], count: 30),
            Tuple(position: "Grafik Tasarımcı", workingTypes: ["Uzaktan"], count: 30),
        ];

        Assert.Equal(6, FacetCountProjector.Project([facet], tuples)
            ["/is-ilanlari/uzaktan-yazilim-muhendisi"]);
    }

    [Fact]
    public void ProvinceIsMatchedExactlyNotBySubstring()
    {
        // The API filters location with an exact province equality, so a substring match here
        // would fold a district or a similarly-named province into the wrong city's count.
        FacetDefinition facet = Facet("/is-ilanlari/afyon", 1, province: "Afyon");

        LiveJobFacetTuple[] tuples =
        [
            Tuple(province: "Afyon", count: 4),
            Tuple(province: "Afyonkarahisar", count: 90),
        ];

        Assert.Equal(4, FacetCountProjector.Project([facet], tuples)["/is-ilanlari/afyon"]);
    }

    [Fact]
    public void EveryFacetGetsACountIncludingZero()
    {
        FacetDefinition[] facets =
        [
            Facet("/is-ilanlari/istanbul", 1, province: "İstanbul"),
            Facet("/is-ilanlari/hakkari", 1, province: "Hakkari"),
        ];

        IReadOnlyDictionary<string, int> counts =
            FacetCountProjector.Project(facets, [Tuple(province: "İstanbul", count: 12)]);

        // A missing key is indistinguishable from a facet that was never a candidate, and the
        // difference matters to the transition diff: one means "this page thinned out and
        // should leave the sitemap", the other means "the manifest shrank".
        Assert.Equal(12, counts["/is-ilanlari/istanbul"]);
        Assert.Equal(0, counts["/is-ilanlari/hakkari"]);
    }

    [Fact]
    public void UnconstrainedFacetsAreSkippedRatherThanMatchingEverything()
    {
        FacetDefinition[] facets =
        [
            Facet("/is-ilanlari", 1),
            Facet("/is-ilanlari/istanbul", 1, province: "İstanbul"),
        ];

        IReadOnlyDictionary<string, int> counts = FacetCountProjector.Project(
            facets,
            [
                Tuple(province: "İstanbul", count: 10),
                Tuple(province: "Ankara", count: 90),
            ]);

        Assert.Equal(0, counts["/is-ilanlari"]);
        Assert.Equal(10, counts["/is-ilanlari/istanbul"]);
    }

    [Fact]
    public void CaseAndDiacriticsDoNotSplitACount()
    {
        // Folding is the one deliberate divergence from the API's raw comparison, and it is
        // in the safe direction: 'tam zamanli' and 'Tam Zamanlı' are the same working
        // preference to a human and to a URL, so a data-entry inconsistency must not drop
        // live jobs out of a count.
        FacetDefinition facet = Facet(
            "/is-ilanlari/tam-zamanli", 1, workingPrefs: ["Tam Zamanlı"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(workingPrefs: ["Tam Zamanlı"], count: 3),
            Tuple(workingPrefs: ["tam zamanli"], count: 2),
            Tuple(workingPrefs: ["TAM ZAMANLI"], count: 1),
        ];

        Assert.Equal(6, FacetCountProjector.Project([facet], tuples)["/is-ilanlari/tam-zamanli"]);
    }

    [Fact]
    public void ProvinceAgnosticFacetsCountAcrossEveryCity()
    {
        // The province bucketing is an optimisation, and this is the case it could get wrong:
        // a facet with no city constraint must be evaluated against every tuple, not only
        // against the ones in some bucket it happens to land in.
        FacetDefinition facet = Facet(
            "/is-ilanlari/yazilim", 1, departments: ["Bilişim"]);

        LiveJobFacetTuple[] tuples =
        [
            Tuple(province: "İstanbul", department: "Bilişim", count: 5),
            Tuple(province: "Ankara", department: "Bilişim", count: 4),
            Tuple(province: "İzmir", department: "Bilişim - İnternet", count: 3),
            Tuple(province: null, department: "Bilişim", count: 1),
        ];

        Assert.Equal(13, FacetCountProjector.Project([facet], tuples)["/is-ilanlari/yazilim"]);
    }
}
