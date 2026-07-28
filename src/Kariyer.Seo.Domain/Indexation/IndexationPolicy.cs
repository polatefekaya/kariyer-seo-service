namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// The live-job-count gate, mirroring the SPA's <c>decideIndexation</c>
/// (<c>src/seo/facets/indexation.ts</c>).
///
/// A facet earns a place in <c>sitemap-jobfilters.xml</c> only if it also earns
/// <c>index,follow</c> on the page itself. Those two decisions MUST agree: a sitemap that
/// advertises a page whose own meta tag says <c>noindex</c> is a self-contradiction Google
/// resolves by trusting the tag and distrusting the sitemap.
///
/// The thresholds are the curation. There is no hand-maintained allowlist of good combos —
/// the count is the signal, and it degrades in the safe direction: a facet that thins out
/// leaves the sitemap on the next rebuild rather than staying advertised as a page with four
/// results on it.
/// </summary>
public static class IndexationPolicy
{
    /// <summary>Single-axis pages (city, sector, position, work-type) need this many live jobs.</summary>
    public const int DefaultSingleAxisMinimum = 5;

    /// <summary>Combos need more, because a thin combo is the classic doorway page.</summary>
    public const int DefaultComboMinimum = 10;

    /// <summary>
    /// Whether a facet with this many live jobs is indexable.
    /// </summary>
    /// <param name="axes">Axis count from the manifest. Anything ≥ 2 is a combo.</param>
    /// <param name="liveJobCount">Live jobs matching the facet.</param>
    /// <param name="thresholds">Configured minimums.</param>
    public static bool IsIndexable(int axes, int liveJobCount, IndexationThresholds thresholds) =>
        liveJobCount >= MinimumFor(axes, thresholds);

    /// <summary>The threshold that applies to a facet with this many axes.</summary>
    public static int MinimumFor(int axes, IndexationThresholds thresholds) =>
        axes >= 2 ? thresholds.Combo : thresholds.SingleAxis;

    /// <summary>Convenience overload for a whole definition.</summary>
    public static bool IsIndexable(
        FacetDefinition facet, int liveJobCount, IndexationThresholds thresholds) =>
        !facet.ConstrainsNothing && IsIndexable(facet.Axes, liveJobCount, thresholds);
}

/// <summary>
/// The two numbers, passed in rather than read from configuration inside the domain.
/// </summary>
/// <param name="SingleAxis">Minimum live jobs for a one-axis facet.</param>
/// <param name="Combo">Minimum live jobs for a two-or-more-axis facet.</param>
public readonly record struct IndexationThresholds(int SingleAxis, int Combo)
{
    /// <summary>The values the SPA ships with. Changing either here without changing
    /// <c>src/seo/facets/indexation.ts</c> puts the sitemap and the page tags in
    /// disagreement about the same facet.</summary>
    public static IndexationThresholds Default { get; } = new(
        IndexationPolicy.DefaultSingleAxisMinimum,
        IndexationPolicy.DefaultComboMinimum);

    /// <summary>
    /// A threshold of zero or less would admit every candidate facet in the manifest —
    /// several thousand of them, most with no jobs at all — and turn the filter sitemap into
    /// a doorway-page farm. Validated at startup, but stated here too because the domain is
    /// where the meaning lives.
    /// </summary>
    public bool IsUsable => SingleAxis > 0 && Combo > 0;
}
