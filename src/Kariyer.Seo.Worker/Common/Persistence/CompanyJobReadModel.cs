namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// A read-only view of the fields this service needs from the Node application's
/// <c>company_job</c> table.
///
/// Keyless and never tracked. The freshness service maps it this way to stop an accidental
/// <c>SaveChanges</c> reaching a table it does not own, and keeps ONE guarded UPDATE as an
/// explicit exception. This service has no such exception: it never writes <c>company_job</c>
/// at all (PLAN §1), which is asserted in <c>RolePlanTests</c> and stated as
/// <see cref="Roles.RolePlan.WritesToCompanyJob"/>.
///
/// The column set is deliberately minimal — only what a sitemap entry or a facet count
/// needs. If a query ever starts depending on a column not listed here, that should be a
/// conscious decision rather than something that happens to work.
/// </summary>
public sealed class CompanyJobReadModel
{
    /// <summary>Primary identity, and the key of every <c>seo_url_state</c> row.</summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>What the SPA routes on. Used verbatim to build the canonical URL.</summary>
    public string? SlugUrl { get; init; }

    public string? Status { get; init; }

    public bool IsActive { get; init; }

    public bool IsDeleted { get; init; }

    /// <summary>Emitted as <c>&lt;lastmod&gt;</c>.</summary>
    public DateTimeOffset? ModifiedOn { get; init; }

    // ── Facet axes ──────────────────────────────────────────────────────────────
    // Note what is NOT here: `industry`. The plan named it, but company_job has no such
    // column — the sector axis is carried by `department`, which is what the jobs API
    // filters on. Mapping a column that does not exist would have failed at the first query;
    // silently substituting one would have produced counts nobody could explain.

    public string? Province { get; init; }

    public string? Town { get; init; }

    public string? Department { get; init; }

    public string? Position { get; init; }

    /// <summary>text[] in Postgres — the "place" axis (remote / office / hybrid).</summary>
    public string[] WorkingType { get; init; } = [];

    /// <summary>text[] in Postgres — the "time" axis (full-time / part-time / internship).</summary>
    public string[] WorkingPrefs { get; init; } = [];
}
