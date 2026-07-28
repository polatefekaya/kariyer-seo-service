namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// Folds the single-pass corpus aggregate onto the facet manifest, producing a live job
/// count per facet path.
///
/// This is the whole of PLAN §7's facet-count requirement, and it is pure: one database
/// query produces <see cref="LiveJobFacetTuple"/> rows, and everything after that happens
/// here, in memory, with no I/O and no clock. Which means the matching rules — the ones that
/// decide whether İstanbul/Yazılım is worth indexing — are asserted in milliseconds against
/// fixtures rather than against a production database nobody wants to run a 3,000-query
/// experiment on.
///
/// <b>Matching rules</b>, each chosen to agree with what the jobs API actually does, because
/// the count the SPA shows is the count its <c>decideIndexation</c> used:
/// <list type="bullet">
///   <item><b>Province</b> — folded equality. The API filters location with an exact
///   <c>province = 'İstanbul'</c>.</item>
///   <item><b>Department / position</b> — folded SUBSTRING, OR'd across the facet's values.
///   The API uses <c>iLike '%value%'</c>, which is why the sector value <c>Bilişim</c>
///   legitimately selects rows whose department is <c>Bilişim - İnternet</c>. Equality here
///   would undercount precisely the curated sector pages the manifest exists to serve.</item>
///   <item><b>working_type / working_prefs</b> — folded equality against any ELEMENT of the
///   array column, OR'd across the facet's values. The API expands these to
///   <c>EXISTS (SELECT 1 FROM unnest(working_type) e WHERE e = 'Uzaktan')</c>.</item>
///   <item>Axes are AND'd. A facet is matched by a job only if every axis it constrains
///   matches.</item>
/// </list>
///
/// Folding rather than the API's raw comparison is the one deliberate divergence, and it is
/// in the safe direction: <c>tam zamanli</c> and <c>Tam Zamanlı</c> are the same working
/// preference to a human and to a URL, and treating them as different would drop live jobs
/// out of a count on a data-entry inconsistency. See <see cref="TurkishFold"/>.
/// </summary>
public static class FacetCountProjector
{
    /// <summary>
    /// Counts live jobs per facet path.
    /// </summary>
    /// <param name="facets">Manifest entries. Entries that constrain nothing are skipped.</param>
    /// <param name="tuples">The aggregate rows. Enumerated once.</param>
    /// <returns>
    /// A count for EVERY facet supplied, including zero. Facets with no jobs must appear —
    /// a missing key is indistinguishable from a facet that was never in the manifest, and
    /// the difference matters: one is "this page thinned out and should leave the sitemap",
    /// the other is "the manifest shrank". <see cref="Indexation.FacetIndexabilityTracker"/>
    /// needs to tell those apart to decide whether to emit a transition event.
    /// </returns>
    public static IReadOnlyDictionary<string, int> Project(
        IReadOnlyList<FacetDefinition> facets,
        IEnumerable<LiveJobFacetTuple> tuples)
    {
        ArgumentNullException.ThrowIfNull(facets);
        ArgumentNullException.ThrowIfNull(tuples);

        FoldedFacet[] folded = [.. facets
            .Where(f => !f.ConstrainsNothing)
            .Select(FoldedFacet.From)];

        Dictionary<string, int> counts = new(facets.Count, StringComparer.Ordinal);

        foreach (FacetDefinition facet in facets)
        {
            counts[facet.Path] = 0;
        }

        // Bucketed by folded province, because that is the axis with by far the highest
        // cardinality (81 provinces) and almost every combo facet constrains it.
        //
        // Without this the fold is |tuples| × |facets| — for a real corpus and a 3,000-entry
        // manifest that is tens of millions of comparisons on every rebuild. With it, a
        // tuple is only ever compared against facets in its own province plus the
        // province-less ones, which is roughly two orders of magnitude fewer.
        Dictionary<string, List<FoldedFacet>> byProvince = new(StringComparer.Ordinal);
        List<FoldedFacet> provinceAgnostic = [];

        foreach (FoldedFacet facet in folded)
        {
            if (facet.Province is null)
            {
                provinceAgnostic.Add(facet);
                continue;
            }

            if (!byProvince.TryGetValue(facet.Province, out List<FoldedFacet>? bucket))
            {
                bucket = [];
                byProvince[facet.Province] = bucket;
            }

            bucket.Add(facet);
        }

        foreach (LiveJobFacetTuple tuple in tuples)
        {
            FoldedTuple candidate = FoldedTuple.From(tuple);

            if (byProvince.TryGetValue(candidate.Province, out List<FoldedFacet>? bucket))
            {
                Accumulate(bucket, candidate, counts);
            }

            Accumulate(provinceAgnostic, candidate, counts);
        }

        return counts;
    }

    private static void Accumulate(
        List<FoldedFacet> facets, in FoldedTuple tuple, Dictionary<string, int> counts)
    {
        foreach (FoldedFacet facet in facets)
        {
            if (facet.Matches(tuple))
            {
                counts[facet.Path] += tuple.JobCount;
            }
        }
    }

    /// <summary>
    /// A manifest entry with every comparison value folded once, up front.
    ///
    /// Folding inside the inner loop instead would re-fold the same few hundred strings
    /// millions of times per rebuild, which is the difference between this being a
    /// sub-second step and being the slowest thing the service does.
    /// </summary>
    private sealed class FoldedFacet
    {
        private FoldedFacet(
            string path,
            string? province,
            string[] departments,
            string[] positions,
            string[] workingTypes,
            string[] workingPrefs)
        {
            Path = path;
            Province = province;
            _departments = departments;
            _positions = positions;
            _workingTypes = workingTypes;
            _workingPrefs = workingPrefs;
        }

        private readonly string[] _departments;
        private readonly string[] _positions;
        private readonly string[] _workingTypes;
        private readonly string[] _workingPrefs;

        public string Path { get; }

        /// <summary>Folded province, or null when this facet does not constrain location.</summary>
        public string? Province { get; }

        public static FoldedFacet From(FacetDefinition facet) => new(
            facet.Path,
            facet.Province is null ? null : TurkishFold.Slug(facet.Province),
            Fold(facet.Departments),
            Fold(facet.Positions),
            Fold(facet.WorkingTypes),
            Fold(facet.WorkingPrefs));

        public bool Matches(in FoldedTuple tuple) =>
            // Province is already guaranteed by the bucket the caller pulled this from.
            MatchesSubstring(_departments, tuple.Department)
            && MatchesSubstring(_positions, tuple.Position)
            && MatchesElement(_workingTypes, tuple.WorkingTypes)
            && MatchesElement(_workingPrefs, tuple.WorkingPrefs);

        /// <summary>An unconstrained axis matches everything; otherwise any value winning is enough.</summary>
        private static bool MatchesSubstring(string[] needles, string haystack)
        {
            if (needles.Length == 0)
            {
                return true;
            }

            foreach (string needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesElement(string[] wanted, string[] actual)
        {
            if (wanted.Length == 0)
            {
                return true;
            }

            foreach (string value in wanted)
            {
                foreach (string element in actual)
                {
                    if (element == value)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Folds and drops empties. An empty filter value would fold to "" and, under a
        /// substring test, match every row — turning a mis-generated manifest entry into a
        /// facet that claims the entire corpus.
        /// </summary>
        private static string[] Fold(IReadOnlyList<string> values) =>
            [.. values.Select(TurkishFold.Slug).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>An aggregate row with its comparison values folded once.</summary>
    private readonly struct FoldedTuple
    {
        private FoldedTuple(
            string province,
            string department,
            string position,
            string[] workingTypes,
            string[] workingPrefs,
            int jobCount)
        {
            Province = province;
            Department = department;
            Position = position;
            WorkingTypes = workingTypes;
            WorkingPrefs = workingPrefs;
            JobCount = jobCount;
        }

        public string Province { get; }

        public string Department { get; }

        public string Position { get; }

        public string[] WorkingTypes { get; }

        public string[] WorkingPrefs { get; }

        public int JobCount { get; }

        public static FoldedTuple From(LiveJobFacetTuple tuple) => new(
            TurkishFold.Slug(tuple.Province),
            TurkishFold.Slug(tuple.Department),
            TurkishFold.Slug(tuple.Position),
            [.. tuple.WorkingTypes.Select(TurkishFold.Slug)],
            [.. tuple.WorkingPrefs.Select(TurkishFold.Slug)],
            tuple.JobCount);
    }
}
