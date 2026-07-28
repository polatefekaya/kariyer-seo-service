using System.Text.Json;
using System.Text.Json.Serialization;
using Kariyer.Seo.Domain.Indexation;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Telemetry;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Common.Facets;

/// <summary>
/// Fetches and caches <c>facet-manifest.json</c> from the web app.
///
/// <b>Why this is fetched rather than hardcoded.</b> The curated city, sector, position and
/// work-type registries live in <c>kariyer-zamani-web</c>, and every path in the manifest is
/// built by the same <c>buildJobFilterPath</c> the SPA's chips, hubs and canonical tags use.
/// A second implementation here would be right on the day it was written and wrong the first
/// time someone adds a sector — producing a sitemap full of URLs that 301 or 404.
///
/// <b>Why the axis VALUES travel with each entry.</b> A path alone is not enough to count
/// anything. The sector slug <c>yazilim</c> selects <c>department</c> values
/// <c>["Bilişim", "Bilişim - İnternet"]</c>, which share no characters with the slug. Deriving
/// the filter from the URL would count zero jobs for exactly the pages that matter most and
/// silently drop them from the sitemap while the SPA went on serving them as
/// <c>index,follow</c>.
///
/// <b>Why failure is never an empty list.</b> An empty manifest is a legitimate instruction —
/// "no facet is a candidate" — so returning one on a failed fetch would publish an empty
/// filter sitemap and de-list every facet page on the site because an unrelated deploy was
/// briefly answering 502. Failures throw; the caller keeps the previous file.
/// </summary>
public sealed class FacetManifestClient(
    HttpClient http,
    IOptions<FacetManifestOptions> options,
    TimeProvider clock,
    ILogger<FacetManifestClient> logger) : IFacetManifestSource
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // Guards the cache. A rebuild is single-threaded, but the diagnostics endpoint can force
    // one concurrently with the cron, and two simultaneous fetches of a 200 KB document are
    // pure waste.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<FacetDefinition>? _cached;
    private DateTimeOffset _fetchedAt;

    public async Task<IReadOnlyList<FacetDefinition>> GetAsync(CancellationToken cancellationToken)
    {
        FacetManifestOptions config = options.Value;

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_cached is not null && clock.GetUtcNow() - _fetchedAt < config.CacheFor)
            {
                DiagnosticsConfig.FacetManifestFetches.Add(1,
                    new KeyValuePair<string, object?>("outcome", "cached"));

                return _cached;
            }

            IReadOnlyList<FacetDefinition> fetched = await FetchAsync(config, cancellationToken);

            _cached = fetched;
            _fetchedAt = clock.GetUtcNow();

            DiagnosticsConfig.FacetManifestFetches.Add(1,
                new KeyValuePair<string, object?>("outcome", "fresh"));

            return fetched;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            DiagnosticsConfig.FacetManifestFetches.Add(1,
                new KeyValuePair<string, object?>("outcome", "failed"));

            if (_cached is not null)
            {
                // Serving a stale candidate set is strictly better than serving none: the
                // manifest only changes when the web app deploys, so a cached copy is almost
                // always still correct, and the count gate is re-applied against live data
                // either way.
                logger.LogWarning(ex,
                    "Facet manifest fetch failed; reusing the copy from {FetchedAt:O}. The "
                    + "candidate set is stale but the live-job counts are not.", _fetchedAt);

                return _cached;
            }

            logger.LogError(ex,
                "Facet manifest fetch failed and nothing is cached. sitemap-jobfilters.xml will "
                + "not be rebuilt this run; the previously published file stays live.");

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<FacetDefinition>> FetchAsync(
        FacetManifestOptions config, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        await using Stream body = await http.GetStreamAsync(config.Url, timeout.Token);

        List<ManifestEntry>? entries =
            await JsonSerializer.DeserializeAsync<List<ManifestEntry>>(body, Json, timeout.Token);

        if (entries is null)
        {
            throw new JsonException("The facet manifest deserialised to null.");
        }

        List<FacetDefinition> definitions = [];
        int rejected = 0;

        foreach (ManifestEntry entry in entries)
        {
            // Validated, not trusted. This document is fetched over HTTP from another
            // repository's build output, so a bad deploy there must not be able to put an
            // absolute URL, a protocol-relative `//evil.example`, or a path traversal into
            // our sitemap — the file Google reads as our statement about our own site.
            if (!FacetUrl.IsFacetPath(entry.Path))
            {
                rejected++;
                continue;
            }

            FacetDefinition definition = new(
                entry.Path!,
                entry.Axes <= 0 ? 1 : entry.Axes,
                string.IsNullOrWhiteSpace(entry.Province) ? null : entry.Province,
                entry.Departments ?? [],
                entry.Positions ?? [],
                entry.WorkingTypes ?? [],
                entry.WorkingPrefs ?? []);

            // An entry that constrains nothing would match the whole corpus, always clear its
            // threshold, and land in the sitemap as a "filter" that filters nothing. Almost
            // always a manifest generated before its registries loaded.
            if (definition.ConstrainsNothing)
            {
                rejected++;
                continue;
            }

            definitions.Add(definition);
        }

        if (rejected > 0)
        {
            logger.LogWarning(
                "Rejected {Rejected} of {Total} facet manifest entries as malformed or "
                + "unconstrained. Check the generator in kariyer-zamani-web.",
                rejected, entries.Count);
        }

        logger.LogInformation(
            "Fetched {Count} candidate facets from {Url}.", definitions.Count, config.Url);

        return definitions;
    }

    /// <summary>
    /// The wire shape.
    ///
    /// Every axis field is optional so an older manifest — one carrying only
    /// <c>path</c> and <c>axes</c> — still deserialises. It will then be rejected as
    /// unconstrained rather than silently counted wrong, which is the correct way for this
    /// service to react to a web app that has not yet deployed the enriched generator.
    /// </summary>
    private sealed class ManifestEntry
    {
        public string? Path { get; init; }

        public int Axes { get; init; }

        /// <summary>company_job.province label for the city axis.</summary>
        public string? Province { get; init; }

        public List<string>? Departments { get; init; }

        public List<string>? Positions { get; init; }

        public List<string>? WorkingTypes { get; init; }

        public List<string>? WorkingPrefs { get; init; }
    }
}
