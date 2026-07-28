namespace Kariyer.Seo.Worker.Common.Roles;

/// <summary>
/// One binary, three jobs (PLAN §5). The role is chosen by <c>SERVICE_ROLE</c> and decides
/// which slices are wired up at startup.
///
/// The split exists for blast radius, not tidiness. Both deployable roles write to R2, and
/// R2 is where a mistake becomes visible to Googlebot; keeping them separable means the
/// expensive full rebuild can later be moved off the pod that has to answer freshness events
/// promptly, without touching a slice.
/// </summary>
public enum ServiceRole
{
    /// <summary>Cron full rebuild and the R2 write that goes with it. One replica.</summary>
    Builder,

    /// <summary>Freshness-event consumers, Garnet purges, and the debounced incremental
    /// flush of <c>sitemap-jobs</c>. One replica.</summary>
    Reactor,

    /// <summary>
    /// Everything in one process.
    ///
    /// Unlike the freshness service — where <c>all</c> is development-only because it puts
    /// the expiry write on every replica — <c>all</c> is the intended LAUNCH configuration
    /// here (PLAN §5), at exactly one replica. The reason is that both other roles write to
    /// R2 anyway, so splitting them buys nothing until there is a reason to scale one
    /// independently. What must stay true either way is the replica count: see
    /// <see cref="RolePlan.WritesToR2"/>.
    /// </summary>
    All,
}
