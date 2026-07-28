using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Kariyer.Seo.Worker.Common.Telemetry;

/// <summary>
/// Stamps the current trace and span onto every log event, so a log line found in SigNoz
/// can be pivoted straight to the trace of the check that produced it. Essential here:
/// explaining why one specific job was expired means correlating the applier's decision
/// back through several checks spread over days.
/// </summary>
public sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("SpanId", activity.SpanId.ToHexString()));

        if (activity.ParentSpanId != default)
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("ParentSpanId", activity.ParentSpanId.ToHexString()));
        }
    }
}
