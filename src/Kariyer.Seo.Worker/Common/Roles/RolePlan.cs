namespace Kariyer.Seo.Worker.Common.Roles;

/// <summary>
/// What a given <see cref="ServiceRole"/> is actually allowed to do.
///
/// Kept as a plain value object rather than a chain of <c>if (role == ...)</c> through
/// Program.cs so the wiring rules are unit-testable without booting a host, and so "does
/// this replica write to R2?" has exactly one place to look.
/// </summary>
/// <param name="Role">The configured role.</param>
/// <param name="RunsFullRebuild">Runs the cron full-corpus rebuild.</param>
/// <param name="ConsumesFreshnessEvents">Consumes JobExpired/JobResurrected.</param>
/// <param name="ConsumesCmsEvents">Consumes CmsPagePublished/CmsPageUnpublished.</param>
/// <param name="RunsDirtyFlush">Runs the debounced incremental re-projection of sitemap-jobs.</param>
public sealed record RolePlan(
    ServiceRole Role,
    bool RunsFullRebuild,
    bool ConsumesFreshnessEvents,
    bool ConsumesCmsEvents,
    bool RunsDirtyFlush)
{
    /// <summary>Every role owns SEO state, so every role needs the database.</summary>
    public bool NeedsDatabase => true;

    /// <summary>Every role either publishes or consumes, so every role needs the bus.</summary>
    public bool NeedsBus => true;

    /// <summary>
    /// Any role that consumes an inbound event purges prerendered HTML — jobs on expiry,
    /// CMS pages on publish and unpublish.
    /// </summary>
    public bool NeedsPrerenderCache => ConsumesFreshnessEvents || ConsumesCmsEvents;

    /// <summary>Only the builder fetches the facet manifest from the web app.</summary>
    public bool NeedsFacetManifest => RunsFullRebuild;

    /// <summary>
    /// True for every role permitted to publish files to R2 — which is every role that
    /// produces a sitemap at all, by either path.
    ///
    /// Surfaced as a named property so it can be asserted on in tests and logged loudly at
    /// startup. It is the dangerous capability in this service: two replicas staging
    /// <c>sitemap.xml</c> concurrently can swap in each other's half-finished index, and the
    /// crawler that fetches during that window is the only party who ever finds out.
    /// </summary>
    public bool WritesToR2 => RunsFullRebuild || RunsDirtyFlush;

    /// <summary>
    /// Always false. Stated as a property, and asserted in
    /// <c>Worker.Tests/Roles/RolePlanTests</c>, because it is the single most important
    /// thing this service does NOT do (PLAN §1).
    ///
    /// <c>company_job</c> belongs to the Node application, and the freshness service owns the
    /// one legitimate write to it behind a mass-expiry safety valve. A sitemap builder with a
    /// write path to that table would be a second author of the same rows with none of that
    /// machinery. Encoding it here means adding such a path breaks a test rather than a
    /// catalogue.
    /// </summary>
    public bool WritesToCompanyJob => false;

    public static RolePlan For(ServiceRole role) => role switch
    {
        ServiceRole.Builder => new RolePlan(role,
            RunsFullRebuild: true, ConsumesFreshnessEvents: false, ConsumesCmsEvents: false,
            RunsDirtyFlush: false),

        ServiceRole.Reactor => new RolePlan(role,
            RunsFullRebuild: false, ConsumesFreshnessEvents: true, ConsumesCmsEvents: true,
            RunsDirtyFlush: true),

        ServiceRole.All => new RolePlan(role,
            RunsFullRebuild: true, ConsumesFreshnessEvents: true, ConsumesCmsEvents: true,
            RunsDirtyFlush: true),

        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unhandled service role."),
    };
}
