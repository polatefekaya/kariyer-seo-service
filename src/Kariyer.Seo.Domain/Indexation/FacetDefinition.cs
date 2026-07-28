namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// One candidate indexable facet page, as published in the web app's
/// <c>public/seo/facet-manifest.json</c>.
///
/// "Candidate" is the operative word: being in the manifest earns a facet a COUNT, not a
/// place in the sitemap. The live-job-count gate in <see cref="IndexationPolicy"/> is the
/// real curation, exactly as it is in the SPA's <c>decideIndexation</c>.
///
/// The axis VALUES travel with the entry rather than being re-derived from the path. That
/// is not redundancy — it is the only correct option. The curated registries live in the web
/// repo, and the mapping from a slug to backend values is neither the identity nor
/// invertible: the sector slug <c>yazilim</c> selects <c>department</c> values
/// <c>["Bilişim", "Bilişim - İnternet"]</c>, which fold to <c>bilisim</c> and
/// <c>bilisim-internet</c> and share not one character with the slug in the URL. A service
/// that matched slugs against column values would count zero jobs for the highest-value
/// pages on the site and quietly drop them from the sitemap while the SPA went on serving
/// them as <c>index,follow</c>.
/// </summary>
/// <param name="Path">Canonical site-relative path, e.g. <c>/is-ilanlari/istanbul/yazilim</c>.</param>
/// <param name="Axes">
/// How many axes the facet constrains (1 = single, 2+ = combo). Drives which threshold
/// applies. Taken from the manifest rather than counted from the value lists below, so the
/// web app remains the authority on what counts as an axis.
/// </param>
/// <param name="Province">
/// <c>company_job.province</c> label for the city axis, or null. The LABEL
/// (<c>İstanbul</c>), not the slug — the slug is a URL artefact and does not appear in the
/// database.
/// </param>
/// <param name="Departments">
/// <c>company_job.department</c> values selecting the sector axis. Any one matching is
/// enough (the SPA sends them as an OR'd list).
/// </param>
/// <param name="Positions"><c>company_job.position</c> values selecting the role axis, OR'd.</param>
/// <param name="WorkingTypes">
/// <c>company_job.working_type</c> values (a text[] column) — the "place" axis:
/// remote / office / hybrid.
/// </param>
/// <param name="WorkingPrefs">
/// <c>company_job.working_prefs</c> values (also text[]) — the "time" axis:
/// full-time / part-time / internship.
/// </param>
public sealed record FacetDefinition(
    string Path,
    int Axes,
    string? Province,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Positions,
    IReadOnlyList<string> WorkingTypes,
    IReadOnlyList<string> WorkingPrefs)
{
    /// <summary>
    /// True when this entry constrains nothing at all.
    ///
    /// Such an entry would match every live job, so its count would be the whole corpus and
    /// it would always clear the threshold — a facet that is in the sitemap because it
    /// filters nothing. Almost always a manifest that was generated before its registries
    /// loaded; treated as a defect and skipped rather than trusted.
    /// </summary>
    public bool ConstrainsNothing =>
        Province is null
        && Departments.Count == 0
        && Positions.Count == 0
        && WorkingTypes.Count == 0
        && WorkingPrefs.Count == 0;
}
