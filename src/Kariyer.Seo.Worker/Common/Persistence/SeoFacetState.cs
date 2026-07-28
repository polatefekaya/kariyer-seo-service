namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// Last computed indexability of one facet (PLAN §8).
///
/// Its only job is to make <c>FacetIndexabilityChangedEvent</c> a TRANSITION event rather
/// than a state dump. Without a record of what was true last time, the rebuild has nothing
/// to compare against and can only republish the current state of all ~3,000 candidates
/// every 45 minutes — which would put a few million pointless messages a month on the
/// exchange and make "a facet became indexable" impossible to alert on.
/// </summary>
public sealed class SeoFacetState
{
    /// <summary>Canonical site-relative facet path.</summary>
    public string FacetPath { get; set; } = string.Empty;

    /// <summary>Axis count from the manifest, kept so a threshold change can be reasoned
    /// about against what was actually applied.</summary>
    public int Axes { get; set; }

    /// <summary>Live job count at the last rebuild.</summary>
    public int JobCount { get; set; }

    /// <summary>Whether it cleared its threshold at the last rebuild.</summary>
    public bool Indexable { get; set; }

    /// <summary>When this row was last recomputed.</summary>
    public DateTimeOffset LastModified { get; set; }
}
