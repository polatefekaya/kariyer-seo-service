using Kariyer.Seo.Domain.Indexation;

namespace Kariyer.Seo.Domain.Ports;

/// <summary>
/// Supplies the candidate facet list published by the web app.
///
/// A port rather than a direct HTTP call inside the rebuild, so the rebuild can be tested
/// against a fixed manifest, and so the fetch policy — caching, what to do when the web app
/// is briefly unreachable — lives in one place in the Worker.
/// </summary>
public interface IFacetManifestSource
{
    /// <summary>
    /// The current candidate facets.
    /// </summary>
    /// <remarks>
    /// Implementations must NOT return an empty list to signal a failed fetch. An empty
    /// manifest is a legitimate instruction — "no facet is a candidate" — and acting on it
    /// would publish an empty <c>sitemap-jobfilters.xml</c>, de-listing every filter page on
    /// the site because a deploy of an unrelated repo was briefly returning 502. Failures
    /// throw, and the caller keeps the previous file.
    /// </remarks>
    Task<IReadOnlyList<FacetDefinition>> GetAsync(CancellationToken cancellationToken);
}
