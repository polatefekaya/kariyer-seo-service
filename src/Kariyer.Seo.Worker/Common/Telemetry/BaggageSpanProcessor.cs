using System.Diagnostics;
using OpenTelemetry;

namespace Kariyer.Seo.Worker.Common.Telemetry;

/// <summary>
/// Copies allow-listed W3C baggage entries onto every span as it starts.
///
/// <b>This service is mostly a CONSUMER of baggage rather than a producer of it.</b> When
/// kariyer-cms-service publishes a page it puts the page's id and path into baggage; the
/// propagator carries them through the RabbitMQ message header, MassTransit restores them around
/// the consumer, and this processor stamps them onto the spans produced here. The result is that
/// the sitemap flush and prerender purge triggered by that publish are findable by
/// <c>cms.page_id</c> — the same filter that finds the publish itself, one service away.
///
/// Without this, the two are joinable only by trace id, which is fine once you already have the
/// trace and useless when the question starts from a URL that is wrong in the sitemap.
///
/// The allow-list is deliberate: baggage rides on every outbound header in the trace, so it is
/// both a cardinality and an exfiltration risk, and only these low-cardinality, non-personal keys
/// are promoted.
/// </summary>
public sealed class BaggageSpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        foreach (KeyValuePair<string, string> entry in Baggage.Current)
        {
            if (CmsBaggage.IsPropagated(entry.Key))
            {
                activity.SetTag(entry.Key, entry.Value);
            }
        }
    }
}

/// <summary>
/// The baggage keys this service accepts from kariyer-cms-service.
/// </summary>
/// <remarks>
/// These strings are duplicated in kariyer-cms-service (which sets them) and kariyer-freshness-service.
/// Kept in sync by convention rather than through Kariyer.Messaging.Contracts — that package is
/// about message shapes, and pulling a telemetry concern into it would couple every consumer's
/// observability to a contracts version bump. A key missing here costs a filter, not correctness.
/// </remarks>
public static class CmsBaggage
{
    public const string PageId = "cms.page_id";
    public const string Path = "cms.path";
    public const string Locale = "cms.locale";

    /// <summary>
    /// Whether a key is accepted. An allow-list rather than "take everything a message offers":
    /// these headers arrive from another service, and promoting arbitrary ones onto every span
    /// would let an upstream change blow up this service's attribute cardinality.
    /// </summary>
    public static bool IsPropagated(string key) =>
        key is PageId or Path or Locale;
}
