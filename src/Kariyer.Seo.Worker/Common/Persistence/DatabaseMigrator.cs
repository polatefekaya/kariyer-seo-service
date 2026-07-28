using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// Applies EF Core migrations at startup, safely, even when several replicas boot together.
///
/// The danger with migrate-on-boot is concurrency: EF's <c>Migrate()</c> is not safe to run
/// from two processes at once against the same database. This wraps it in a Postgres
/// <b>session-level advisory lock</b> on a fixed key — whichever replica reaches it first
/// applies the migrations, and every other replica blocks on the same key, then acquires it,
/// finds the history table already current, and applies nothing.
///
/// The key differs from the freshness service's. That is deliberate: both services migrate
/// their own schema in the SAME database, and sharing a key would make each one's rollout
/// wait on the other's for no reason.
///
/// Opt out via <c>Persistence:MigrateOnStartup</c> to keep the stricter "migrate as a
/// separate one-shot Job with elevated rights" posture, in which case the running service
/// needs no DDL permissions at all.
/// </summary>
public static class DatabaseMigrator
{
    // A fixed, arbitrary 64-bit key, distinct from the freshness service's.
    private const long AdvisoryLockKey = 0x5E0_1D_C4A2_77B1;

    public static async Task MigrateAsync(
        IServiceProvider services, string connectionString, ILogger logger, CancellationToken ct)
    {
        // A dedicated connection holds the lock for the whole migration. It must be the SAME
        // session that unlocks it, so it stays open across the Migrate() call — which opens
        // its own connection underneath, and that is fine.
        await using NpgsqlConnection lockConnection = new(connectionString);
        await lockConnection.OpenAsync(ct);

        logger.LogInformation("Acquiring migration advisory lock…");

        await using (NpgsqlCommand acquire = new("SELECT pg_advisory_lock(@key)", lockConnection))
        {
            acquire.Parameters.AddWithValue("key", AdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            SeoDbContext db = scope.ServiceProvider.GetRequiredService<SeoDbContext>();

            List<string> toApply = [.. await db.Database.GetPendingMigrationsAsync(ct)];

            if (toApply.Count == 0)
            {
                logger.LogInformation("Database is up to date; no migrations to apply.");
                return;
            }

            logger.LogInformation(
                "Applying {Count} migration(s): {Migrations}", toApply.Count, string.Join(", ", toApply));

            await db.Database.MigrateAsync(ct);

            logger.LogInformation("Migrations applied successfully.");
        }
        finally
        {
            // Released explicitly rather than relying on the session closing, so the next
            // waiting replica proceeds immediately.
            await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(@key)", lockConnection);
            release.Parameters.AddWithValue("key", AdvisoryLockKey);
            await release.ExecuteNonQueryAsync(ct);
        }
    }
}
