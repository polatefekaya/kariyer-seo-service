using System.Net;
using System.Text;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Facets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kariyer.Seo.Worker.Tests.Facets;

/// <summary>
/// The manifest is fetched over HTTP from another repository's build output, so this client
/// is a trust boundary as much as a cache.
/// </summary>
public sealed class FacetManifestClientTests
{
    private const string ValidJson = """
        [
          {
            "path": "/is-ilanlari/istanbul",
            "axes": 1,
            "province": "İstanbul",
            "departments": [],
            "positions": [],
            "workingTypes": [],
            "workingPrefs": []
          },
          {
            "path": "/is-ilanlari/istanbul/yazilim",
            "axes": 2,
            "province": "İstanbul",
            "departments": ["Bilişim", "Bilişim - İnternet"],
            "positions": [],
            "workingTypes": [],
            "workingPrefs": []
          }
        ]
        """;

    [Fact]
    public async Task ParsesTheEnrichedShape()
    {
        FacetManifestClient client = Client(ValidJson, out _);

        IReadOnlyList<FacetDefinition> facets = await client.GetAsync(CancellationToken.None);

        Assert.Equal(2, facets.Count);

        FacetDefinition sector = facets[1];
        Assert.Equal("/is-ilanlari/istanbul/yazilim", sector.Path);
        Assert.Equal(2, sector.Axes);
        Assert.Equal("İstanbul", sector.Province);

        // The values, not the slug. This is the whole reason the manifest carries them: the
        // sector slug 'yazilim' shares no characters with the department values it selects.
        Assert.Equal(["Bilişim", "Bilişim - İnternet"], sector.Departments);
    }

    [Fact]
    public async Task RejectsEntriesThatCouldPoisonTheSitemap()
    {
        const string hostile = """
            [
              { "path": "https://evil.example/is-ilanlari", "axes": 1, "province": "İstanbul" },
              { "path": "//evil.example/is-ilanlari", "axes": 1, "province": "İstanbul" },
              { "path": "/is-ilanlari/../admin", "axes": 1, "province": "İstanbul" },
              { "path": "/sirketler", "axes": 1, "province": "İstanbul" },
              { "path": "/is-ilanlari/ankara", "axes": 1, "province": "Ankara" }
            ]
            """;

        FacetManifestClient client = Client(hostile, out _);

        // A bad deploy in the web repo must not be able to put an absolute URL, a
        // protocol-relative host, or a traversal into the file Google reads as our statement
        // about our own site.
        FacetDefinition only = Assert.Single(
            await client.GetAsync(CancellationToken.None));

        Assert.Equal("/is-ilanlari/ankara", only.Path);
    }

    [Fact]
    public async Task RejectsUnconstrainedEntries()
    {
        // An old manifest — path and axes only — deserialises fine and then constrains
        // nothing. Such an entry would match every live job, always clear its threshold, and
        // land in the sitemap as a "filter" that filters nothing. Dropping it is how this
        // service reacts to a web app that has not deployed the enriched generator yet.
        const string legacy = """[{ "path": "/is-ilanlari/istanbul", "axes": 1 }]""";

        FacetManifestClient client = Client(legacy, out _);

        Assert.Empty(await client.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASecondCallInsideTheWindowIsServedFromCache()
    {
        FacetManifestClient client = Client(ValidJson, out CountingHandler handler);

        await client.GetAsync(CancellationToken.None);
        await client.GetAsync(CancellationToken.None);

        // The manifest only changes when the web app deploys, so re-fetching a 200 KB
        // document on every cron tick is pure waste — and it would make a rebuild depend on
        // the web app being up at that exact minute.
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AFailedRefreshReusesTheLastGoodManifest()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));
        CountingHandler handler = new(ValidJson);
        FacetManifestClient client = Client(handler, clock);

        Assert.Equal(2, (await client.GetAsync(CancellationToken.None)).Count);

        handler.Fail = true;
        clock.Advance(TimeSpan.FromHours(7));

        // Stale candidates are strictly better than none: the live-job counts are re-applied
        // against fresh data either way, and returning an empty list would publish an empty
        // filter sitemap and de-list every facet page because an unrelated repo was briefly
        // answering 502.
        Assert.Equal(2, (await client.GetAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task AFailureWithNothingCachedThrows()
    {
        CountingHandler handler = new(ValidJson) { Fail = true };

        FacetManifestClient client = Client(handler, new FakeTimeProvider());

        // It must NOT degrade to an empty list. An empty manifest is a legitimate
        // instruction — "no facet is a candidate" — and acting on one here would publish an
        // empty sitemap-jobfilters.xml. Throwing leaves the previously published file live.
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync(CancellationToken.None));
    }

    private static FacetManifestClient Client(string json, out CountingHandler handler)
    {
        handler = new CountingHandler(json);
        return Client(handler, new FakeTimeProvider());
    }

    private static FacetManifestClient Client(CountingHandler handler, TimeProvider clock) =>
        new(new HttpClient(handler),
            Options.Create(new FacetManifestOptions
            {
                Url = "https://kariyerzamani.com/seo/facet-manifest.json",
                CacheFor = TimeSpan.FromHours(6),
            }),
            clock,
            NullLogger<FacetManifestClient>.Instance);

    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            if (Fail)
            {
                throw new HttpRequestException("The web app is deploying.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
