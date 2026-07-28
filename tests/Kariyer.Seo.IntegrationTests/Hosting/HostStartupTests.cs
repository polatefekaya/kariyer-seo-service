using Kariyer.Seo.Worker.Common.Roles;
using Npgsql;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Kariyer.Seo.IntegrationTests.Hosting;

/// <summary>
/// Boots the real host as <c>SERVICE_ROLE=all</c> against real Postgres and RabbitMQ.
///
/// This is the test that catches what no unit test can: a DI graph that does not resolve, a
/// migration that does not apply, a hosted service that throws on start, an options validator
/// that rejects the shipped defaults. Every one of those is a crash loop at deploy time and a
/// green build before it.
///
/// It also proves the shape PLAN §5 names as the launch configuration actually starts.
///
/// It uses its OWN, empty Postgres rather than the shared fixture. That is the whole point:
/// the shared fixture applies docs/schema.sql as raw DDL, which leaves no migration history,
/// so the host would boot, find its tables already present and its history empty, and fail
/// trying to create them again. Booting against an empty database exercises the path a real
/// deployment takes — DatabaseMigrator, the advisory lock, and the migration itself.
/// </summary>
public sealed class HostStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("seo_host_tests").Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        // The corpus table the read-only projection queries. The host does not create it —
        // it belongs to the Node application — so without it a diagnostics call that counts
        // live jobs would fail for the right reason at the wrong time.
        await ExecuteAsync(await File.ReadAllTextAsync(CompanyJobStandInPath()));
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    [Fact]
    public async Task BootsAsAllAndAnswersLiveness()
    {
        await using SeoApplication app = Create(ServiceRole.All);

        using HttpClient client = app.CreateClient();

        // Liveness has no dependencies by design: a pod must not be killed and restarted
        // just because Postgres is briefly unreachable.
        HttpResponseMessage health = await client.GetAsync("/health");

        Assert.True(health.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ReadinessIncludesTheDatabaseAndTheBroker()
    {
        await using SeoApplication app = Create(ServiceRole.All);

        using HttpClient client = app.CreateClient();

        // Polled rather than asserted once. Readiness is genuinely eventually-consistent at
        // boot: MassTransit reports its bus degraded until the connection and every receive
        // endpoint are up, which is exactly the behaviour that makes it a useful readiness
        // signal — and exactly what would make a single immediate assertion flaky.
        HttpResponseMessage ready = await PollAsync(client, "/health/ready");

        Assert.True(
            ready.IsSuccessStatusCode,
            $"Readiness never became healthy: {await ready.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task ExposesPrometheusMetrics()
    {
        await using SeoApplication app = Create(ServiceRole.All);

        using HttpClient client = app.CreateClient();

        HttpResponseMessage metrics = await client.GetAsync("/metrics");

        Assert.True(metrics.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TheDiagnosticsEndpointReportsTheRoleAndTheR2Responsibility()
    {
        await using SeoApplication app = Create(ServiceRole.All);

        using HttpClient client = app.CreateClient();

        HttpResponseMessage status = await client.GetAsync("/api/seo/diag/sitemap");

        Assert.True(status.IsSuccessStatusCode);

        string body = await status.Content.ReadAsStringAsync();

        Assert.Contains("\"writesToR2\":true", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARebuildRequestOnAReactorIsRefused()
    {
        await using SeoApplication app = Create(ServiceRole.Reactor);

        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/seo/diag/rebuild", null);

        // Not a courtesy 404. A rebuild on a pod that is not the designated builder would be
        // a SECOND concurrent R2 writer — exactly what the single-writer rule prevents.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MigrationsApplyOnBoot()
    {
        await using SeoApplication app = Create(ServiceRole.All);

        // Forces the host to build and run its startup path.
        _ = app.Services.GetRequiredService<RolePlan>();

        // The migration history table lands in OUR schema, not public — both this service and
        // the freshness service migrate into the same database, and a shared history table in
        // public would have each one seeing the other's migrations as unknown.
        long applied = await ScalarAsync(
            """
            SELECT COUNT(*) FROM information_schema.tables
             WHERE table_schema = 'seo' AND table_name = '__ef_migrations_history'
            """);

        Assert.Equal(1, applied);

        // And the tables it describes actually exist.
        Assert.Equal(3, await ScalarAsync(
            """
            SELECT COUNT(*) FROM information_schema.tables
             WHERE table_schema = 'seo'
               AND table_name IN ('seo_url_state', 'seo_facet_state', 'seo_rebuild_log')
            """));
    }

    private static async Task<HttpResponseMessage> PollAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);

        for (int i = 0; i < 40 && !response.IsSuccessStatusCode; i++)
        {
            await Task.Delay(250);
            response = await client.GetAsync(path);
        }

        return response;
    }

    private SeoApplication Create(ServiceRole role) =>
        new(role, _postgres.GetConnectionString(), _rabbit.GetConnectionString());

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = new(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string CompanyJobStandInPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Kariyer.Seo.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "deploy", "smoke", "company_job_standin.sql");
    }

    /// <summary>
    /// The real <c>Program</c>, configured the way a deployment would be.
    ///
    /// Nothing is stubbed out except R2 and Garnet, both of which are switched OFF through
    /// the same configuration a deployment uses rather than through a test-only seam — so if
    /// those switches stop working, this test notices.
    /// </summary>
    private sealed class SeoApplication(string role, string postgres, string rabbit)
        : WebApplicationFactory<Program>
    {
        public SeoApplication(ServiceRole role, string postgres, string rabbit)
            : this(role.ToString().ToLowerInvariant(), postgres, rabbit)
        {
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            Environment.SetEnvironmentVariable(ServiceRoleResolver.EnvironmentVariable, role);

            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = postgres,
                    ["ConnectionStrings:RabbitMQ"] = rabbit,

                    // R2 credentials are required of any role that writes, so they are
                    // supplied — the sink is never exercised here, only constructed.
                    ["Seo:R2:Endpoint"] = "https://account.r2.cloudflarestorage.com",
                    ["Seo:R2:Bucket"] = "kariyer-seo-tests",
                    ["Seo:R2:AccessKey"] = "test",
                    ["Seo:R2:SecretKey"] = "test",

                    // Off through the real switch, not through a substituted service.
                    ["Garnet:Enabled"] = "false",
                    ["Indexing:Enabled"] = "false",

                    // Long enough that no background tick fires during the test and races the
                    // assertions. The workers' behaviour is covered by their own suites.
                    ["Seo:CronInterval"] = "24:00:00",
                    ["Seo:DebounceWindow"] = "00:05:00",
                }));

            return base.CreateHost(builder);
        }
    }
}
