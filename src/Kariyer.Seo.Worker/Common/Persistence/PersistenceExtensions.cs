using Kariyer.Seo.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// Registers the state store.
///
/// Column names are stated explicitly in the entity configurations rather than derived by a
/// naming convention, so the snake_case mapping is visible where it matters and cannot shift
/// under a package upgrade — which for the read-only <c>company_job</c> projection would mean
/// silently querying columns that do not exist on someone else's table.
/// </summary>
public static class PersistenceExtensions
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<SeoDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", SeoDbContext.Schema)));

        services.AddScoped<ISeoStore, SeoStore>();

        // The domain's corpus port is the SAME instance as the store, resolved through the
        // interface it already implements. Registering a second implementation would let the
        // rebuild read the corpus on one DbContext while writing its state on another —
        // outside the transaction, against a different snapshot.
        services.AddScoped<IJobCorpusReader>(sp => sp.GetRequiredService<ISeoStore>());
        services.AddScoped<ICmsPageReader>(sp => sp.GetRequiredService<ISeoStore>());

        return services;
    }
}
