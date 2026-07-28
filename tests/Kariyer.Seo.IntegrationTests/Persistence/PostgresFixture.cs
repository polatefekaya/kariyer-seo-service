using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kariyer.Seo.IntegrationTests.Persistence;

/// <summary>
/// An ephemeral Postgres for the tests that must run real SQL.
///
/// These exist because the most consequential statements in this service cannot be verified
/// by any amount of unit testing. The facet aggregate groups on two <c>text[]</c> columns;
/// the corpus sync is a set-based upsert plus a NOT EXISTS retirement. Their correctness IS
/// their interaction with Postgres — a mock would only confirm that the SQL says what I meant
/// it to say, not that what I meant is right.
///
/// The schema comes from <c>docs/schema.sql</c>, the same artefact that ships, so these tests
/// fail if the checked-in DDL and the code drift apart.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("seo_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await ExecuteAsync(await File.ReadAllTextAsync(RepositoryFile("docs", "schema.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(
            RepositoryFile("deploy", "smoke", "company_job_standin.sql")));

        // cms.seo_page belongs to kariyer-cms-service. This service reads it through a
        // keyless projection and never creates it, so the tests need a stand-in exactly as
        // they do for company_job.
        await ExecuteAsync(await File.ReadAllTextAsync(
            RepositoryFile("deploy", "smoke", "cms_seo_page_standin.sql")));
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public SeoDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SeoDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public SeoStore CreateStore(SeoDbContext context) =>
        new(context, Options.Create(new PersistenceOptions()), NullLogger<SeoStore>.Instance);

    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Wipes state between tests so ordering cannot make one test depend on another.</summary>
    public Task ResetAsync() => ExecuteAsync(
        """
        TRUNCATE seo.seo_url_state, seo.seo_facet_state, seo.seo_rebuild_log;
        TRUNCATE public.company_job;
        TRUNCATE cms.seo_page;
        """);

    /// <summary>Inserts one CMS page into the stand-in.</summary>
    public Task SeedCmsPageAsync(
        string path,
        string status = "published",
        bool published = true,
        bool noindex = false,
        DateTimeOffset? publishedAt = null) =>
        ExecuteAsync($"""
            INSERT INTO cms.seo_page (id, path, status, published_layout, noindex, published_at)
            VALUES (gen_random_uuid(), '{path.Replace("'", "''")}', '{status}',
                    {(published ? "'{\"blocks\":[]}'::jsonb" : "NULL")},
                    {noindex.ToString().ToLowerInvariant()},
                    {(publishedAt is null ? "NULL" : $"'{publishedAt:O}'::timestamptz")})
            """);

    /// <summary>
    /// Inserts one live job into the corpus stand-in.
    /// </summary>
    public Task SeedJobAsync(
        string uid,
        string slug,
        string status = "approved",
        bool isActive = true,
        string? province = null,
        string? department = null,
        string? position = null,
        string[]? workingType = null,
        string[]? workingPrefs = null,
        DateTimeOffset? modifiedOn = null)
    {
        string array(string[]? values) =>
            values is null || values.Length == 0
                ? "'{}'"
                : "ARRAY[" + string.Join(",", values.Select(v => $"'{v.Replace("'", "''")}'")) + "]::text[]";

        string quoted(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

        return ExecuteAsync($"""
            INSERT INTO public.company_job
                (uid, slug_url, status, is_active, is_deleted, modified_on,
                 province, town, department, position, working_type, working_prefs)
            VALUES ('{uid}', '{slug}', '{status}', {isActive.ToString().ToLowerInvariant()}, false,
                    {(modifiedOn is null ? "NULL" : $"'{modifiedOn:O}'::timestamptz")},
                    {quoted(province)}, NULL, {quoted(department)}, {quoted(position)},
                    {array(workingType)}, {array(workingPrefs)})
            """);
    }

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Kariyer.Seo.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");

        return Path.Combine([root, .. parts]);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
