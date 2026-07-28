namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// One row of the single-pass facet aggregate: a distinct combination of the facetable
/// columns, plus how many live jobs share it.
///
/// This is the shape that makes PLAN §7's "never one query per facet" achievable. The
/// alternative reading of that rule — one aggregate per AXIS — still cannot answer a combo
/// facet, because <c>count(city) × count(sector)</c> is not <c>count(city AND sector)</c>.
/// Grouping by the whole tuple instead gives a single query whose result set is bounded by
/// the number of DISTINCT tuples (thousands), not by the corpus (hundreds of thousands), and
/// from which every facet count — single-axis and combo alike — is a pure in-memory fold.
///
/// One query, one pass, arbitrary facets. Three thousand manifest entries cost three
/// thousand queries the naive way, and one query here.
/// </summary>
/// <param name="Province"><c>company_job.province</c>.</param>
/// <param name="Department"><c>company_job.department</c>.</param>
/// <param name="Position"><c>company_job.position</c>.</param>
/// <param name="WorkingTypes"><c>company_job.working_type</c>, a text[] column.</param>
/// <param name="WorkingPrefs"><c>company_job.working_prefs</c>, a text[] column.</param>
/// <param name="JobCount">Live jobs with exactly this tuple.</param>
public sealed record LiveJobFacetTuple(
    string? Province,
    string? Department,
    string? Position,
    IReadOnlyList<string> WorkingTypes,
    IReadOnlyList<string> WorkingPrefs,
    int JobCount);
