using Kariyer.Seo.Worker.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kariyer.Seo.Worker.Tests.Persistence;

/// <summary>
/// Pins the DDL the EF model produces against a checked-in file.
///
/// <c>docs/schema.sql</c> is generated FROM the model, committed, and asserted on every run,
/// so a mapping change shows up as a failing test and a readable SQL diff in code review
/// rather than as a surprise at deploy time. It is also the artefact the integration tests
/// apply, which means those tests fail if the checked-in DDL and the code drift apart.
/// </summary>
public sealed class SchemaScriptTests
{
    private static readonly string SchemaPath = Path.Combine(RepositoryRoot(), "docs", "schema.sql");

    [Fact]
    public void TheCheckedInSchemaMatchesTheModel()
    {
        string generated = Normalise(GenerateScript());

        if (!File.Exists(SchemaPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchemaPath)!);
            File.WriteAllText(SchemaPath, Header + generated);
            Assert.Fail($"Wrote {SchemaPath}. Review the SQL and commit it, then re-run.");
        }

        string committed = Normalise(StripHeader(File.ReadAllText(SchemaPath)));

        Assert.True(
            committed == generated,
            $"The EF model no longer matches {SchemaPath}. Delete the file and re-run this test "
            + "to regenerate it, then review the SQL diff — a schema change should be a "
            + "deliberate, reviewed act, not a side effect of an entity edit.");
    }

    [Fact]
    public void CompanyJobIsNotInOurSchema()
    {
        // The read-only projection is mapped with ToView(null) precisely so EF never treats
        // it as ours. Without that, migrations would CREATE a second, empty company_job
        // inside the seo schema — same name, right columns, no data — and every corpus read
        // would quietly return nothing while the service reported perfect health.
        string script = GenerateScript();

        Assert.DoesNotContain("company_job", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOutboxAndInboxTablesArePresent()
    {
        // Their absence would not fail a build or a boot — it would fail the first publish,
        // at runtime, on the path that tells the estate a sitemap changed.
        string script = GenerateScript();

        Assert.Contains("InboxState", script, StringComparison.Ordinal);
        Assert.Contains("OutboxState", script, StringComparison.Ordinal);
        Assert.Contains("OutboxMessage", script, StringComparison.Ordinal);
    }

    private static string GenerateScript()
    {
        DbContextOptions<SeoDbContext> options = new DbContextOptionsBuilder<SeoDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;

        using SeoDbContext context = new(options);
        return context.Database.GenerateCreateScript();
    }

    private const string Header =
        """
        -- Generated from the EF Core model by SchemaScriptTests. Do not hand-edit.
        -- Regenerate by deleting this file and re-running the test.
        --
        -- This service OWNS the seo schema and nothing else. company_job belongs to the Node
        -- application and is reached ONLY through a read-only, keyless projection — there is
        -- no write path to it anywhere in this service, guarded and reviewable or otherwise.
        --
        -- Applied automatically at startup by DatabaseMigrator, serialised across replicas by
        -- a Postgres advisory lock. Set Persistence:MigrateOnStartup=false to apply this file
        -- as a one-shot job instead, in which case the running service needs no DDL rights.

        CREATE SCHEMA IF NOT EXISTS seo;

        """;

    private static string StripHeader(string content)
    {
        const string marker = "CREATE SCHEMA IF NOT EXISTS seo;";
        int index = content.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? content : content[(index + marker.Length)..];
    }

    private static string Normalise(string sql) =>
        string.Join('\n', sql
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Kariyer.Seo.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
