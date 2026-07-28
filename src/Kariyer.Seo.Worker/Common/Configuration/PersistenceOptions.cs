using System.ComponentModel.DataAnnotations;

namespace Kariyer.Seo.Worker.Common.Configuration;

/// <summary>
/// Where this service's own tables live, and where the job table it reads lives.
///
/// Both are configuration because they are a seam: when the .NET job service lands, the job
/// read repoints and nothing else in this service changes.
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>Schema owning <c>seo_url_state</c>, <c>seo_facet_state</c>,
    /// <c>seo_rebuild_log</c> and the MassTransit inbox/outbox. This service migrates and
    /// owns everything here.</summary>
    [Required]
    public string Schema { get; init; } = "seo";

    /// <summary>Schema containing the Node application's <c>company_job</c>. We are a guest
    /// here, and a read-only one.</summary>
    [Required]
    public string CompanyJobSchema { get; init; } = "public";

    /// <summary>
    /// Schema owned by <c>kariyer-cms-service</c>, holding <c>seo_page</c>. Also read-only.
    ///
    /// Configurable for the same reason as the one above: it is a seam. If the CMS ever moves
    /// to its own database, this is one of the two edges that repoint — and the corresponding
    /// URL source degrades to an HTTP fetch of the CMS's paths endpoint, nothing more.
    /// </summary>
    [Required]
    public string CmsSchema { get; init; } = "cms";

    /// <summary>
    /// Apply EF Core migrations on startup, serialised across replicas by a Postgres
    /// advisory lock. Turn OFF to migrate as a separate one-shot Job, in which case the
    /// running service needs no DDL rights at all.
    /// </summary>
    public bool MigrateOnStartup { get; init; } = true;

    /// <summary>
    /// Rows fetched per round-trip when streaming the corpus.
    ///
    /// Exists because the default behaviour of the Npgsql provider is to buffer a whole
    /// result set, which for a 400k-row corpus defeats the streaming that
    /// <see cref="Domain.Sitemaps.SitemapWriter"/> is built around.
    /// </summary>
    [Range(100, 100_000)]
    public int StreamBatchSize { get; init; } = 5_000;

    /// <summary>Rows upserted per statement when syncing <c>seo_url_state</c>.</summary>
    [Range(100, 100_000)]
    public int UpsertBatchSize { get; init; } = 2_000;
}

/// <summary>The prerender cache (Garnet) this service purges on expiry.</summary>
public sealed class GarnetOptions
{
    public const string SectionName = "Garnet";

    /// <summary>StackExchange.Redis connection string, e.g. <c>garnet:6379</c>.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Whether purging is enabled at all.
    ///
    /// Explicit rather than inferred from an empty connection string, because "no Garnet
    /// configured" and "Garnet deliberately off" must not look the same at startup: the
    /// first is a misconfiguration that leaves withdrawn jobs serving cached apply pages for
    /// a full TTL, and there is no metric that distinguishes it from a quiet day.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
