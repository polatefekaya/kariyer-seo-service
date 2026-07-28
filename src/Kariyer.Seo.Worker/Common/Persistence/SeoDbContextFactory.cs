using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// Lets the <c>dotnet ef</c> tooling construct the context at design time — to generate and
/// script migrations — without booting the application.
///
/// The app's host cannot be used for this: it demands a <c>SERVICE_ROLE</c>, live connection
/// strings and a reachable R2 bucket, and would try to stand up the whole service just to
/// read a model. This factory builds only what EF needs. The connection string is never
/// connected to during <c>migrations add</c> or <c>migrations script</c>; a placeholder is
/// enough, and a real one is picked up from the environment when a command genuinely touches
/// the database.
/// </summary>
public sealed class SeoDbContextFactory : IDesignTimeDbContextFactory<SeoDbContext>
{
    public SeoDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=seo_design_time;Username=postgres;Password=postgres";

        DbContextOptions<SeoDbContext> options =
            new DbContextOptionsBuilder<SeoDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql
                    // Match the runtime configuration exactly, so the migrations-history
                    // table lands in our own schema rather than public — where it would
                    // collide with the freshness service's, since both services migrate
                    // into the same database.
                    .MigrationsHistoryTable("__ef_migrations_history", SeoDbContext.Schema))
                .Options;

        return new SeoDbContext(options);
    }
}
