using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Kariyer.Seo.Domain.Robots;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Roles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Tests.Configuration;

/// <summary>
/// What configuration actually binds to, using the real <c>appsettings.json</c> that ships
/// inside the image.
///
/// <b>Why a suite of its own.</b> <c>SeoOptionsTests</c> builds settings in memory, which is
/// the right instrument for validation rules and the wrong one for this: it never puts the
/// C# defaults and the JSON file in the same binder, and that collision was the whole bug.
/// Both path lists had non-empty initialisers, the binder APPENDS bound values to a
/// pre-populated collection rather than replacing it, and so every entry present in both
/// survived twice. The live bucket served a <c>sitemap-static-1.xml.gz</c> with 14
/// <c>&lt;url&gt;</c> entries for 7 pages — duplicate <c>&lt;loc&gt;</c> being invalid per
/// the sitemaps protocol — and every <c>Disallow</c> line in robots.txt appeared twice.
///
/// Nothing reported it. It was found by reading the published output. So the rule this file
/// enforces is: bind what SHIPS, through the same layers the host uses, and assert the
/// effective list.
///
/// The audit behind <see cref="NoBoundOptionsTypeCarriesANonEmptyCollectionDefault"/>: these
/// two properties are the only configuration-bound collections in the service. Every other
/// options class — <c>R2Options</c>, <c>ThresholdOptions</c>, <c>FacetManifestOptions</c>,
/// <c>IndexingOptions</c>, <c>RabbitOptions</c>, <c>EventsOptions</c>,
/// <c>PersistenceOptions</c>, <c>GarnetOptions</c> — is scalars only. That test is what keeps
/// it that way rather than leaving the next collection property to find out in production.
/// </summary>
public sealed class ConfigurationBindingTests
{
    /// <summary>
    /// Every options type the host binds to a configuration section. Kept next to the
    /// reflection test that consumes it, because its value is being exhaustive.
    /// </summary>
    private static readonly Type[] BoundOptionsTypes =
    [
        typeof(SeoOptions), typeof(R2Options), typeof(ThresholdOptions),
        typeof(FacetManifestOptions), typeof(PersistenceOptions), typeof(GarnetOptions),
        typeof(RabbitOptions), typeof(EventsOptions), typeof(IndexingOptions),
    ];

    [Fact]
    public void TheShippedFileBindsEveryStaticPathExactlyOnce()
    {
        SeoOptions seo = BindShipped();

        // The exact list, in order, from appsettings.json — not a count, and not a
        // distinct-count. A count would have passed while every entry was doubled, and
        // asserting on the deduplicated set would test the assertion rather than the binding.
        Assert.Equal(
            [
                "/", "/sirketler", "/cv", "/isveren", "/hakkimizda", "/iletisim",
                "/sikca-sorulan-sorular",
            ],
            seo.StaticPaths);
    }

    [Fact]
    public void TheShippedFileBindsEveryDisallowedPathExactlyOnce()
    {
        SeoOptions seo = BindShipped();

        Assert.Equal(
            ["/api/", "/hesabim", "/isveren/panel", "/admin", "/cms-preview"],
            seo.DisallowedPaths);
    }

    [Fact]
    public void TheShippedFileStillDisallowsTheCmsPreviewRoute() =>
        // Guarded here as well as in RebuildAllTests, and the difference matters: that suite
        // proves the pipeline puts DisallowedPaths into robots.txt, over its own configuration.
        // This proves the value is in the file that actually deploys. /cms-preview renders
        // UNPUBLISHED drafts on the public origin, so losing this line in a config edit is
        // exactly the kind of silent regression nothing else would surface.
        Assert.Contains("/cms-preview", BindShipped().DisallowedPaths);

    [Fact]
    public void AnEnvironmentVariableLayerReplacesAnEntryRatherThanAppendingToIt()
    {
        // The host reads appsettings.json then environment variables, so this is the ordering
        // that runs in the container. An index already present in the file is REPLACED — one
        // entry changes and the count does not move.
        SeoOptions seo = Bind(new Dictionary<string, string?>
        {
            ["Seo__StaticPaths__1"] = "/kurumsal",
        });

        Assert.Equal(7, seo.StaticPaths.Count);
        Assert.Equal("/kurumsal", seo.StaticPaths[1]);
        Assert.DoesNotContain("/sirketler", seo.StaticPaths);
    }

    [Fact]
    public void AnEnvironmentVariableCanExtendTheShippedList()
    {
        // Appending at the next free index is the supported way to add a path per-deployment
        // without editing the image. Worth pinning: it is also the way someone could
        // accidentally re-add an entry the file already has, which is what the duplicate
        // validator in SeoOptionsExtensions now refuses at boot.
        SeoOptions seo = Bind(new Dictionary<string, string?>
        {
            ["Seo__StaticPaths__7"] = "/kariyer-rehberi",
        });

        Assert.Equal(8, seo.StaticPaths.Count);
        Assert.Equal("/kariyer-rehberi", seo.StaticPaths[7]);
    }

    [Fact]
    public void TheShippedFileStoresSitemapsUncompressedBecauseCloudflareFrontsTheBucket()
    {
        // Pinned because flipping it changes the URLs, and the party that has to agree lives
        // in another system: with compression on, the stored keys and the index children end
        // in .xml.gz and the Cloudflare route has to be repointed in the same change or the
        // index becomes a list of 404s. Nothing in this service can check that, so the value
        // is at least held still here.
        Assert.False(BindShipped().R2.Compress);
    }

    [Fact]
    public void NoBoundOptionsTypeCarriesANonEmptyCollectionDefault()
    {
        // The general form of the bug, rather than the two instances of it.
        //
        // A collection property with entries in its initialiser binds to defaults PLUS
        // configuration, never configuration alone, because the binder appends. There is no
        // syntax that makes it replace, and nothing at runtime reports the result — it is a
        // list that is quietly wrong. So the rule is structural: bound collections start
        // empty, and the values live in appsettings.json.
        List<string> offenders = [];

        foreach (Type type in BoundOptionsTypes)
        {
            object instance = Activator.CreateInstance(type)!;

            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(string)
                    || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                if (property.GetValue(instance) is IEnumerable value && value.GetEnumerator().MoveNext())
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These bound options properties initialise to a non-empty collection: "
            + $"{string.Join(", ", offenders)}. The configuration binder APPENDS bound values "
            + "to a pre-populated collection instead of replacing it, so the C# defaults and "
            + "the appsettings.json entries would both survive and every shared entry would be "
            + "published twice — which is how sitemap-static shipped with each URL in it twice. "
            + "Initialise to [] and put the values in appsettings.json.");
    }

    [Fact]
    public void TheRealEnvironmentVariableProviderLayersOverTheShippedFileTheSameWay()
    {
        // Every other test in this class simulates the environment layer with an in-memory one
        // whose keys are already colon-separated, on the stated assumption that `__` → `:` is
        // the only thing AddEnvironmentVariables does. This is the test that stops that being
        // an assumption: the genuine provider, over a genuine process variable, asserted to
        // land in the same place.
        //
        // It is also what the acceptance criterion asks for literally — the effective list,
        // through the JSON and environment-variable layers the host actually composes.
        using EnvironmentScope environment = new(("Seo__StaticPaths__1", "/kurumsal"));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedAppSettings(), optional: false)
            .AddInMemoryCollection(Flatten(DeploymentEnvironment))
            .AddEnvironmentVariables()
            .Build();

        SeoOptions seo = Resolve(configuration);

        Assert.Equal(7, seo.StaticPaths.Count);
        Assert.Equal("/kurumsal", seo.StaticPaths[1]);
        Assert.DoesNotContain("/sirketler", seo.StaticPaths);
    }

    [Fact]
    public void ADuplicateInjectedThroughTheEnvironmentFailsStartup()
    {
        // The remaining route to the production defect now that the initialisers are empty: an
        // override landing on a free index with a value the shipped file already carries. It
        // is the easiest mistake left to make — adding a path per-deployment without checking
        // the image for it — and it is refused at boot rather than deduplicated.
        OptionsValidationException error = Assert.Throws<OptionsValidationException>(
            () => Bind(new Dictionary<string, string?> { ["Seo__StaticPaths__7"] = "/cv" }));

        string message = string.Join(' ', error.Failures);

        Assert.Contains("Seo:StaticPaths", message, StringComparison.Ordinal);
        Assert.Contains("APPENDS", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStaticSitemapBuiltFromTheShippedFileNamesEachUrlExactlyOnce()
    {
        // Through the real writer, because the thing that was wrong was a published FILE, not
        // a list in memory. Duplicate <loc> is invalid per the sitemaps protocol, and Google's
        // response to it is not an error that reaches anyone here.
        SeoOptions seo = BindShipped();

        Dictionary<string, MemoryStream> files = [];

        // The writer disposes each chunk stream it is handed — that is its contract with the
        // R2 sink, and what writes the gzip trailer in production. MemoryStream.ToArray keeps
        // working afterwards, which is the only reason a plain one is enough here.
        SitemapWriter.WriteUrlSets(
            SitemapNames.StaticBase,
            seo.StaticPaths.Select(p => SitemapUrl.At(SiteUrls.Absolute(seo.SiteUrl, p))),
            fileName => files[fileName] = new MemoryStream());

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        string[] locations =
        [
            .. files.Values.SelectMany(s =>
                XDocument.Parse(Encoding.UTF8.GetString(s.ToArray()))
                    .Descendants(ns + "loc")
                    .Select(e => e.Value)),
        ];

        // 7, not 14. The live bucket served 14.
        Assert.Equal(seo.StaticPaths.Count, locations.Length);
        Assert.Equal(locations.Distinct(StringComparer.Ordinal).Count(), locations.Length);
        Assert.Contains("https://kariyerzamani.com/hakkimizda", locations);
    }

    [Fact]
    public void RobotsTxtBuiltFromTheShippedFileNamesEachDisallowExactlyOnce()
    {
        SeoOptions seo = BindShipped();

        string robots = RobotsPolicy.Build(
            seo.SiteUrl, "/" + SitemapNames.Index, seo.DisallowedPaths, seo.AllowIndexing);

        string[] lines =
        [
            .. robots.Split('\n').Where(l => l.StartsWith("Disallow:", StringComparison.Ordinal)),
        ];

        Assert.Equal(seo.DisallowedPaths.Count, lines.Length);
        Assert.Equal(lines.Distinct(StringComparer.Ordinal).Count(), lines.Length);

        // Named explicitly: the one entry here that guards content rather than crawl budget.
        Assert.Contains("Disallow: /cms-preview", lines);
    }

    /// <summary>
    /// Sets process environment variables and restores exactly what was there.
    ///
    /// Only <see cref="TheRealEnvironmentVariableProviderLayersOverTheShippedFileTheSameWay"/>
    /// needs this, and it is why the rest of the suite does not: the environment is
    /// process-wide state that xUnit's parallel test classes share. This is the only class in
    /// the assembly that reads it, and xUnit runs the tests within a class one at a time.
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _restore;

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            _restore = [.. variables.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))];

            foreach ((string name, string value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? previous) in _restore)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }

    /// <summary>
    /// What a real deployment supplies through the environment rather than through the image.
    ///
    /// <c>appsettings.json</c> ships the credentials blank on purpose, so binding it alone
    /// does not produce a startable configuration — and these tests resolve through the same
    /// validators the host runs, so they need the same inputs it gets. Nothing here overlaps
    /// with the collections under test.
    /// </summary>
    private static readonly Dictionary<string, string?> DeploymentEnvironment = new(StringComparer.Ordinal)
    {
        ["Seo__R2__Endpoint"] = "https://account.r2.cloudflarestorage.com",
        ["Seo__R2__Bucket"] = "kariyer-seo",
        ["Seo__R2__AccessKey"] = "key",
        ["Seo__R2__SecretKey"] = "secret",
        ["Garnet__ConnectionString"] = "localhost:6379",
    };

    private static SeoOptions BindShipped() => Bind([]);

    /// <summary>
    /// The host's own layering: the shipped <c>appsettings.json</c>, then environment
    /// variables. Resolved through <c>AddSeoOptions</c> and <c>IStartupValidator</c> so the
    /// validators run exactly as they do at boot — a binding test that skipped them could
    /// assert a list the service would refuse to start on.
    /// </summary>
    private static SeoOptions Bind(Dictionary<string, string?> overrides)
    {
        Dictionary<string, string?> environment = new(DeploymentEnvironment, StringComparer.Ordinal);

        foreach ((string key, string? value) in overrides)
        {
            environment[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedAppSettings(), optional: false)
            .AddInMemoryCollection(Flatten(environment))
            .Build();

        return Resolve(configuration);
    }

    /// <summary>
    /// Runs the host's own options pipeline over a built configuration — <c>AddSeoOptions</c>
    /// and then <c>IStartupValidator</c>, which is what <c>ValidateOnStart</c> does at boot. A
    /// binding test that skipped the validators could assert a list the service would refuse
    /// to start on.
    /// </summary>
    private static SeoOptions Resolve(IConfiguration configuration)
    {
        ServiceCollection services = [];
        services.AddSeoOptions(configuration, RolePlan.For(ServiceRole.All));

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();

        return provider.GetRequiredService<IOptions<SeoOptions>>().Value;
    }

    /// <summary>
    /// Rewrites <c>Seo__StaticPaths__0</c> into <c>Seo:StaticPaths:0</c>, which is the only
    /// thing <c>AddEnvironmentVariables</c> does to a key. Written in the double-underscore
    /// form in the tests above so they read as what an operator would actually put in a
    /// compose file, without this suite mutating real process environment variables — which
    /// xUnit's parallel test classes share.
    /// </summary>
    private static Dictionary<string, string?> Flatten(Dictionary<string, string?> environment) =>
        environment.ToDictionary(
            e => e.Key.Replace("__", ":", StringComparison.Ordinal),
            e => e.Value,
            StringComparer.Ordinal);

    /// <summary>
    /// The real file, linked into the test output by the csproj rather than copied, so a path
    /// added to appsettings.json is a path this suite asserts on.
    /// </summary>
    private static string ShippedAppSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        Assert.True(
            File.Exists(path),
            $"The shipped appsettings.json was not found at {path}. It is linked into this "
            + "project by Kariyer.Seo.Worker.Tests.csproj; without it these tests would silently "
            + "fall back to binding nothing, which is the failure mode they exist to prevent.");

        return path;
    }
}
