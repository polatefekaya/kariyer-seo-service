using Kariyer.Messaging.Contracts.Cms;
using Kariyer.Messaging.Contracts.Freshness;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Messaging;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Roles;
using Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Kariyer.Seo.IntegrationTests.Messaging;

/// <summary>
/// The two inbound wires, against a REAL RabbitMQ.
///
/// Every other consumer test in this repo uses MassTransit's in-memory harness, which is fine
/// for consumer LOGIC and proves nothing at all about topology: it never creates an exchange,
/// never binds a queue, and never cares what <c>SetEntityName</c> was given. So the single
/// most consequential thing about this service's inputs — that its queues are actually bound
/// to the exchanges two other repositories publish to — was untested until this file.
///
/// The failure it guards is the worst kind this service has. A mismatched exchange name does
/// not throw, does not fail a health check and does not appear in any metric. The queue is
/// created, binds to nothing, and stays empty forever. Jobs quietly stop leaving the sitemap
/// and CMS pages quietly stop arriving, while every dashboard stays green.
///
/// So the producer side here is configured the way the OTHER services configure it — by
/// exchange name and contract type, not by sharing code with the consumer — and the assertion
/// is on the observable effect, not on a harness counter.
/// </summary>
public sealed class RabbitTopologyTests : IAsyncLifetime
{
    private const string Site = "https://kariyerzamani.com";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("seo_topology_tests").Build();

    // The management image, not the plain one: two of these tests assert on the broker's own
    // view of its topology (which queues and exchanges exist) rather than on our configuration
    // object, and that needs the management API on 15672.
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithPortBinding(15672, assignRandomHostPort: true)
        .Build();

    private ServiceProvider _consumerSide = null!;
    private ServiceProvider _producerSide = null!;
    private RecordingCache _cache = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        await ExecuteAsync(await File.ReadAllTextAsync(RepositoryFile("docs", "schema.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(
            RepositoryFile("deploy", "smoke", "company_job_standin.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(
            RepositoryFile("deploy", "smoke", "cms_seo_page_standin.sql")));

        _consumerSide = BuildConsumerSide();
        _producerSide = BuildProducerSide();

        await _consumerSide.GetRequiredService<IBusControl>().StartAsync();
        await _producerSide.GetRequiredService<IBusControl>().StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _producerSide.DisposeAsync();
        await _consumerSide.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    [Fact]
    public async Task AJobExpiredEventPublishedByTheFreshnessServiceReachesThisService()
    {
        await ExecuteAsync(
            """
            INSERT INTO seo.seo_url_state (job_uid, slug, status, last_modified, dirty, updated_at)
            VALUES ('job-wire', 'slug-wire', 0, NULL, false, now())
            """);

        await _producerSide.GetRequiredService<IBus>().Publish(new JobExpiredEvent
        {
            JobUid = "job-wire",
            SlugUrl = "slug-wire",
            Reason = "withdrawn",
            ExpiredAt = DateTimeOffset.UtcNow,
        });

        // Asserted on the EFFECT, over a real broker: the row flipped, which can only happen
        // if the exchange existed, the queue was bound to it, and the message deserialised
        // into the same contract type on both sides.
        Assert.True(
            await EventuallyAsync(async () =>
                await ScalarAsync<int>(
                    "SELECT COUNT(*)::int FROM seo.seo_url_state "
                    + "WHERE job_uid = 'job-wire' AND status = 1") == 1),
            "JobExpiredEvent never reached the consumer. The queue is bound to the wrong "
            + "exchange, or the contract namespaces have diverged.");
    }

    [Fact]
    public async Task ACmsPagePublishedEventPublishedByTheCmsServiceReachesThisService()
    {
        await _producerSide.GetRequiredService<IBus>().Publish(new CmsPagePublishedEvent
        {
            MessageId = "wire-1",
            PageId = Guid.NewGuid().ToString(),
            Path = "/kariyer-rehberi/wire",
            Indexable = true,
            Locales = ["tr"],
            PublishedAt = DateTimeOffset.UtcNow,
        });

        Assert.True(
            await EventuallyAsync(() =>
                Task.FromResult(_cache.Purged.Contains(
                    $"prerender:{Site}/kariyer-rehberi/wire"))),
            "CmsPagePublishedEvent never reached the consumer. The queue is bound to the wrong "
            + "exchange, or the contract namespaces have diverged.");
    }

    [Fact]
    public async Task APathChangePurgesBothTheOldAndTheNewUrl()
    {
        await _producerSide.GetRequiredService<IBus>().Publish(new CmsPagePublishedEvent
        {
            MessageId = "wire-2",
            PageId = Guid.NewGuid().ToString(),
            Path = "/kariyer-rehberi/new-address",
            PreviousPath = "/kariyer-rehberi/old-address",
            Indexable = true,
            Locales = ["tr"],
            PublishedAt = DateTimeOffset.UtcNow,
        });

        // The old URL is the one that matters. Without this purge the prerenderer keeps
        // serving a fully rendered page at an address the CMS no longer resolves, for its
        // whole TTL, and nothing in either service notices.
        Assert.True(
            await EventuallyAsync(() => Task.FromResult(
                _cache.Purged.Contains($"prerender:{Site}/kariyer-rehberi/old-address")
                && _cache.Purged.Contains($"prerender:{Site}/kariyer-rehberi/new-address"))),
            "A moved CMS page did not purge both addresses.");
    }

    [Fact]
    public async Task TheTwoSourcesUseSeparateQueues()
    {
        // Independence is the reason for two queues rather than one: a CMS outage must not
        // stall job expiries. Asserted through the management API so it is a fact about the
        // broker, not about our configuration object.
        string queues = await RabbitApiAsync("/api/queues/%2F");

        Assert.Contains("seo.freshness.consumer", queues, StringComparison.Ordinal);
        Assert.Contains("seo.cms.consumer", queues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryExpectedExchangeExists()
    {
        string exchanges = await RabbitApiAsync("/api/exchanges/%2F");

        // The four inbound names are contracts with two other repositories. A typo in any of
        // them is silent: the queue binds to nothing and stays empty forever.
        foreach (string exchange in new[]
                 {
                     "freshness.job.expired",
                     "freshness.job.resurrected",
                     "cms.page.published",
                     "cms.page.unpublished",
                 })
        {
            Assert.Contains(exchange, exchanges, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The SEO service's REAL messaging configuration — <see cref="MessagingExtensions"/>,
    /// not a hand-rolled approximation. That is the whole point: a test that re-declared the
    /// topology would pass while production was misconfigured.
    /// </summary>
    private ServiceProvider BuildConsumerSide()
    {
        _cache = new RecordingCache();

        ServiceCollection services = [];

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPrerenderCache>(_cache);
        services.AddSingleton<IIndexingSubmitter, DisabledIndexingSubmitter>();
        services.AddSingleton<DirtySignal>();

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SeoOptions
        {
            SiteUrl = Site,
            R2 = new R2Options { Compress = false },
        }));
        services.AddSingleton(
            Microsoft.Extensions.Options.Options.Create(new PersistenceOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RabbitOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new EventsOptions()));

        services.AddDbContext<SeoDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<ISeoStore, SeoStore>();

        services.AddMessaging(
            RolePlan.For(ServiceRole.Reactor), _rabbit.GetConnectionString());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Stands in for the freshness and CMS services.
    ///
    /// It declares only the exchange names and the contract types, exactly as those
    /// repositories do — deliberately sharing no code with the consumer side, so agreement
    /// has to be real rather than assumed.
    /// </summary>
    private ServiceProvider BuildProducerSide()
    {
        ServiceCollection services = [];

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddMassTransit(bus => bus.UsingRabbitMq((_, cfg) =>
        {
            cfg.Host(new Uri(_rabbit.GetConnectionString()));

            cfg.Message<JobExpiredEvent>(m => m.SetEntityName("freshness.job.expired"));
            cfg.Message<JobResurrectedEvent>(m => m.SetEntityName("freshness.job.resurrected"));
            cfg.Message<CmsPagePublishedEvent>(m => m.SetEntityName("cms.page.published"));
            cfg.Message<CmsPageUnpublishedEvent>(m => m.SetEntityName("cms.page.unpublished"));
        }));

        return services.BuildServiceProvider();
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<string> RabbitApiAsync(string path)
    {
        // Credentials come from the container's own connection string, not hardcoded.
        // Testcontainers generates a random user/password per run, so `guest:guest` earns a
        // 401 and a test failure that looks like a topology problem but is not one.
        Uri amqp = new(_rabbit.GetConnectionString());
        string credentials = Uri.UnescapeDataString(amqp.UserInfo);

        using HttpClient client = new()
        {
            BaseAddress = new Uri($"http://localhost:{_rabbit.GetMappedPublicPort(15672)}"),
            DefaultRequestHeaders =
            {
                Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials))),
            },
        };

        return await client.GetStringAsync(path);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = new(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Kariyer.Seo.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            [directory?.FullName ?? throw new InvalidOperationException("Repo root not found."),
             .. parts]);
    }

    private sealed class RecordingCache : IPrerenderCache
    {
        private readonly List<string> _purged = [];

        public IReadOnlyList<string> Purged
        {
            get
            {
                lock (_purged)
                {
                    return [.. _purged];
                }
            }
        }

        public Task<int> PurgeJobAsync(string slug, CancellationToken ct) =>
            Record(Domain.Urls.PrerenderKeys.For(Site, slug));

        public Task<int> PurgePathAsync(string path, CancellationToken ct) =>
            Record([Domain.Urls.PrerenderKeys.ForPath(Site, path)]);

        private Task<int> Record(string[] keys)
        {
            lock (_purged)
            {
                _purged.AddRange(keys);
            }

            return Task.FromResult(keys.Length);
        }
    }
}
