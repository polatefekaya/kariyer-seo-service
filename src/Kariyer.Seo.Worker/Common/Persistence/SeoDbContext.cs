using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// The service's own state store.
///
/// Everything it OWNS lives in the <c>seo</c> schema — its own namespace in the shared jobs
/// database, so its tables can never collide with the Node application's and so permissions,
/// backups and eventual extraction can be reasoned about as one unit.
///
/// It is co-located with <c>company_job</c> rather than given a database of its own for one
/// specific reason: the read model, the local state and the outbox then share a single
/// connection and a single transaction. That is what lets a freshness event's state change
/// and the resulting event be one commit, and what lets a rebuild read the corpus and write
/// its log without a distributed transaction. A separate database would buy isolation and
/// pay for it with the exact atomicity PLAN §6 is built on.
///
/// The two things it does NOT own are <c>public.company_job</c> and <c>cms.seo_page</c>, both
/// reached through keyless read-only projections. Unlike the freshness service there is no
/// guarded write exception for either: this service never writes another schema at all.
/// </summary>
public sealed class SeoDbContext(DbContextOptions<SeoDbContext> options) : DbContext(options)
{
    public const string Schema = "seo";

    public DbSet<SeoUrlState> UrlStates => Set<SeoUrlState>();

    public DbSet<SeoFacetState> FacetStates => Set<SeoFacetState>();

    public DbSet<SeoRebuildLog> RebuildLog => Set<SeoRebuildLog>();

    /// <summary>Read-only projection over the Node application's <c>company_job</c>.</summary>
    public DbSet<CompanyJobReadModel> CompanyJobs => Set<CompanyJobReadModel>();

    /// <summary>Read-only projection over <c>cms.seo_page</c>, owned by kariyer-cms-service.</summary>
    public DbSet<CmsPageReadModel> CmsPages => Set<CmsPageReadModel>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(SeoDbContext).Assembly);

        // The inbox and the outbox both live in this context, and both are load-bearing.
        //
        // INBOX: the freshness consumers are the only path by which a job leaves the sitemap
        // between rebuilds. RabbitMQ delivers at-least-once, so without dedup a redelivered
        // JobExpired would re-run the removal and the purge. Those happen to be idempotent
        // today — but "our correctness depends on every consumer staying accidentally
        // idempotent forever" is not a property worth relying on, and the inbox makes
        // redelivery a genuine no-op at the transaction boundary instead.
        //
        // OUTBOX: SitemapRebuiltEvent is written in the same commit as the rebuild-log row
        // it describes. Without it, a broker outage between the R2 swap and the publish
        // leaves a sitemap that changed and an estate that was never told — and nothing in
        // the system knows the event was lost.
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();

        base.OnModelCreating(builder);
    }
}
