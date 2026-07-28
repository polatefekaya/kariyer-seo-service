using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.IntegrationTests.Persistence;
using Kariyer.Seo.Worker.Common.Persistence;

namespace Kariyer.Seo.IntegrationTests.Sitemaps;

/// <summary>
/// The read-only projection over <c>cms.seo_page</c>, against real SQL.
///
/// These tests pin a rule this service DUPLICATES from another one:
/// <c>Kariyer.Cms.Domain.Pages.PublicationPolicy.IsIndexable</c>. That duplication is a real
/// cost, accepted so a rebuild never depends on the CMS being reachable — and it is only safe
/// while something asserts the two still agree. That something is this file.
///
/// If the CMS ever changes what "indexable" means, these tests are what should fail.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CmsPageProjectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OnlyPublishedNonNoindexPagesWithASnapshotAreProjected()
    {
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/yes");

        // Each of the three exclusions the CMS's own predicate applies.
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/draft", status: "draft");
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/archived", status: "archived");
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/noindex", noindex: true);

        // Published but with a null published_layout. Should be impossible — the publish
        // endpoint writes both in one commit — but advertising it would put an empty page in
        // front of a crawler, so the projection re-checks rather than trusting.
        await postgres.SeedCmsPageAsync("/kariyer-rehberi/no-snapshot", published: false);

        IReadOnlyList<string> paths = await ProjectAsync();

        Assert.Equal(["/kariyer-rehberi/yes"], paths);
    }

    [Fact]
    public async Task ANoindexPageStaysOutOfTheSitemapButIsNotTreatedAsDeleted()
    {
        // The distinction the CMS's PublicationPolicy draws deliberately: noindex means "do
        // not advertise", not "does not exist". A campaign or thank-you page is still a real
        // page a visitor can open — this service simply keeps it out of the sitemap.
        await postgres.SeedCmsPageAsync("/kampanya/tesekkurler", noindex: true);

        Assert.Empty(await ProjectAsync());

        // And it is genuinely still published in the CMS's own terms.
        int published = await postgres.ScalarAsync<int>(
            "SELECT COUNT(*)::int FROM cms.seo_page WHERE status = 'published'");

        Assert.Equal(1, published);
    }

    [Fact]
    public async Task PagesAreOrderedByPath()
    {
        // Load-bearing, not cosmetic: an unordered read changes the file's checksum on every
        // run, which defeats the conditional-write short-circuit and re-uploads a file
        // nothing changed in.
        await postgres.SeedCmsPageAsync("/rehber/c");
        await postgres.SeedCmsPageAsync("/rehber/a");
        await postgres.SeedCmsPageAsync("/rehber/b");

        Assert.Equal(["/rehber/a", "/rehber/b", "/rehber/c"], await ProjectAsync());
    }

    [Fact]
    public async Task LastModComesFromPublishedAt()
    {
        DateTimeOffset publishedAt = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        await postgres.SeedCmsPageAsync("/rehber/x", publishedAt: publishedAt);

        await using SeoDbContext db = postgres.CreateContext();
        SeoStore store = postgres.CreateStore(db);

        List<CmsPage> pages = [];

        await foreach (CmsPage page in store.StreamIndexablePagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal(publishedAt, Assert.Single(pages).LastModified);
    }

    [Fact]
    public async Task CountMatchesTheProjection()
    {
        await postgres.SeedCmsPageAsync("/rehber/a");
        await postgres.SeedCmsPageAsync("/rehber/b");
        await postgres.SeedCmsPageAsync("/rehber/draft", status: "draft");

        await using SeoDbContext db = postgres.CreateContext();
        SeoStore store = postgres.CreateStore(db);

        // The diagnostics endpoint and the sitemap must agree. A count computed by a
        // different predicate would have an operator comparing two numbers that were never
        // meant to match and concluding the projection is broken.
        Assert.Equal(2, await store.CountIndexablePagesAsync(CancellationToken.None));
    }

    private async Task<IReadOnlyList<string>> ProjectAsync()
    {
        await using SeoDbContext db = postgres.CreateContext();
        SeoStore store = postgres.CreateStore(db);

        List<string> paths = [];

        await foreach (CmsPage page in store.StreamIndexablePagesAsync(CancellationToken.None))
        {
            paths.Add(page.Path);
        }

        return paths;
    }
}
