using System.Diagnostics;
using Kariyer.Seo.Worker.Common.Telemetry;
using MassTransit;
using OpenTelemetry;

namespace Kariyer.Seo.Worker.Common.Messaging;

/// <summary>
/// Lifts the CMS baggage headers off an incoming message and back into ambient
/// <see cref="Baggage"/>, so every span this service produces while handling that message carries
/// the originating page's identity.
///
/// <b>This is the receiving half of a hand-rolled propagation, and it exists because MassTransit
/// does not do it.</b> Its instrumentation writes exactly one header — <c>MT-Activity-Id</c>, the
/// W3C traceparent — so the TRACE crosses the broker and baggage does not. The gap is easy to miss
/// because trace-level correlation keeps working: spans here still join the publishing service's
/// trace, and nothing looks broken. What silently does not happen is <c>cms.page_id</c> arriving,
/// which is the part that makes the work findable from a page rather than from a trace id somebody
/// already has. kariyer-cms-service sets these headers explicitly in <c>CmsEventPublisher</c>.
///
/// Restored into Baggage rather than tagged straight onto the consume span, because the point is
/// the spans BELOW it — the Garnet purge, the sitemap read, the R2 write. Baggage is ambient and
/// reaches all of them through <see cref="BaggageSpanProcessor"/>; a tag on one span reaches
/// nothing else.
/// </summary>
public static class BaggageRestoration
{
    /// <summary>
    /// Registers the restore step on the bus-wide consume pipe, so it applies to every consumer on
    /// every endpoint — including ones added later, which is the point of doing it here rather
    /// than per-endpoint.
    /// </summary>
    public static void UseCmsBaggage(this IBusFactoryConfigurator configurator) =>
        configurator.UseExecute(Restore);

    private static void Restore(ConsumeContext context)
    {
        // `object`, not `object?`: MassTransit's GetAll() yields KeyValuePair<string, object>,
        // and declaring the loop variable as nullable makes the compiler warn about a variance
        // mismatch (CS8619) rather than accept it. The `is not string` guard below already
        // handles a null value, so nothing is lost by matching the source type exactly.
        foreach (KeyValuePair<string, object> header in context.Headers.GetAll())
        {
            if (!CmsBaggage.IsPropagated(header.Key) || header.Value is not string value)
            {
                continue;
            }

            Baggage.SetBaggage(header.Key, value);

            // Also stamped on the consume span itself. That span was started by MassTransit before
            // this filter runs, so the processor has already been and gone for it — without this
            // line the one span naming the message would be the only span in the trace WITHOUT
            // the page id on it.
            Activity.Current?.SetTag(header.Key, value);
        }
    }
}
