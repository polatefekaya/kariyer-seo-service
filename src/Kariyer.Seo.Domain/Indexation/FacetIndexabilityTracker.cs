namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// Works out which facets have CHANGED indexability since the last rebuild, so the service
/// emits <c>FacetIndexabilityChangedEvent</c> for transitions only.
///
/// Pure, and separated from the rebuild for a reason worth stating: the alternative — the
/// rebuild publishing the current state of every facet — would put several thousand messages
/// on the exchange every 45 minutes, of which essentially none carry news. Subscribers would
/// have to diff them to find the one facet that actually crossed the line, and "a facet
/// became indexable" would stop being something anyone could alert on.
/// </summary>
public static class FacetIndexabilityTracker
{
    /// <summary>
    /// Compares the newly computed indexability against what was last recorded.
    /// </summary>
    /// <param name="current">Freshly computed indexability and count, per facet path.</param>
    /// <param name="previous">
    /// Last recorded indexability, per facet path, from <c>seo_facet_state</c>.
    /// </param>
    /// <returns>
    /// One entry per genuine transition, ordered by path so a rebuild is reproducible and a
    /// test can assert on a sequence.
    /// </returns>
    public static IReadOnlyList<FacetIndexabilityChange> Diff(
        IReadOnlyDictionary<string, FacetIndexability> current,
        IReadOnlyDictionary<string, bool> previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        List<FacetIndexabilityChange> changes = [];

        foreach ((string path, FacetIndexability state) in current)
        {
            if (previous.TryGetValue(path, out bool was) && was == state.Indexable)
            {
                continue;
            }

            // A facet seen for the first time is only reported when it is indexable.
            //
            // Reporting first-sight NON-indexable facets would fire an event for every one
            // of the ~3,000 candidates on the very first rebuild — and again for every new
            // candidate the web app adds — announcing "this page you have never heard of is
            // still not indexable". The interesting first-sight transition is the one that
            // arrives already earning its place.
            if (!previous.ContainsKey(path) && !state.Indexable)
            {
                continue;
            }

            changes.Add(new FacetIndexabilityChange(path, state.Indexable, state.JobCount));
        }

        changes.Sort(static (a, b) => string.CompareOrdinal(a.FacetPath, b.FacetPath));

        return changes;
    }
}

/// <summary>Computed state of one facet in this rebuild.</summary>
/// <param name="Indexable">Whether it cleared the threshold for its axis count.</param>
/// <param name="JobCount">The count behind the decision, carried so a surprising flip
/// can be explained without re-running the aggregate.</param>
public readonly record struct FacetIndexability(bool Indexable, int JobCount);

/// <summary>One facet crossing the threshold, in either direction.</summary>
public readonly record struct FacetIndexabilityChange(string FacetPath, bool Indexable, int JobCount);
