using OpenTelemetry.Exporter;

namespace Kariyer.Seo.Worker.Common.Telemetry;

/// <summary>
/// How this service talks to the collector, in one place.
///
/// <b>The protocol is stated explicitly and must stay that way.</b> The OTel SDK's default is
/// gRPC on <c>:4317</c>; the collectors this estate actually runs speak HTTP/protobuf on
/// <c>:4318</c>, which is what <c>kariyer-identity-security-service</c> and
/// <c>kariyer-file-service</c> have always exported. Leaving the default in place produces a
/// service that starts cleanly, logs nothing unusual, serves traffic perfectly — and never
/// appears in SigNoz, because an OTLP exporter that cannot reach its collector drops batches in
/// the background and does not surface the failure on any signal the service itself emits. The
/// only symptom is absence, and absence is exactly what a broken exporter and a quiet service
/// look like from the dashboard.
///
/// The per-signal paths are appended here rather than left to the SDK: once
/// <see cref="OtlpExporterOptions.Endpoint"/> is assigned, the SDK treats the value as final and
/// appends nothing, so an endpoint without <c>/v1/traces</c> silently posts every span to the
/// collector's root and gets a 404 it never reports.
/// </summary>
public static class OtlpTransport
{
    /// <summary>HTTP/protobuf, not the SDK's gRPC default. See the type remarks.</summary>
    public const string DefaultEndpoint = "http://localhost:4318";

    /// <summary>
    /// Applies endpoint, protocol and headers for one signal.
    /// </summary>
    /// <param name="signal">
    /// <c>TRACES</c>, <c>METRICS</c> or <c>LOGS</c>. Used both for the standard per-signal
    /// environment override and for the URL path, which the OTLP spec keeps in step.
    /// </param>
    public static void Configure(
        OtlpExporterOptions options, string baseEndpoint, string signal, string headers)
    {
        options.Endpoint = new Uri(SignalEndpoint(baseEndpoint, signal));
        options.Protocol = OtlpExportProtocol.HttpProtobuf;

        if (!string.IsNullOrWhiteSpace(headers))
        {
            options.Headers = headers;
        }
    }

    /// <summary>
    /// The full URL for one signal. <c>OTEL_EXPORTER_OTLP_{SIGNAL}_ENDPOINT</c> wins outright when
    /// set — that is the OTLP spec's precedence, and it is the escape hatch for sending one signal
    /// somewhere else (a metrics backend that is not SigNoz, say) without a code change.
    /// </summary>
    private static string SignalEndpoint(string baseEndpoint, string signal)
    {
        string? perSignal =
            Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT");

        return string.IsNullOrWhiteSpace(perSignal)
            ? baseEndpoint.TrimEnd('/') + "/v1/" + signal.ToLowerInvariant()
            : perSignal;
    }
}
