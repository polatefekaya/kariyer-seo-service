using Kariyer.Seo.Worker.Common.Roles;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Kariyer.Seo.Worker.Common.Telemetry;

/// <summary>
/// Wires Serilog + OpenTelemetry traces, metrics and logs to the same resource identity,
/// matching the shape used by the other Kariyer services so everything lands in SigNoz
/// under one convention.
///
/// The service role is added as a resource attribute. It matters less here than in the
/// freshness service — this one launches as a single `all` replica — but it is what makes
/// the eventual builder/reactor split observable on day one rather than after the dashboards
/// have to be rebuilt.
/// </summary>
public static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder, ServiceRole role)
    {
        string deploymentEnvironment =
            Environment.GetEnvironmentVariable("DEPLOYMENT_ENVIRONMENT")
            ?? builder.Configuration["Observability:DeploymentEnvironment"]
            ?? builder.Environment.EnvironmentName;

        string serviceVersion =
            builder.Configuration["Observability:ServiceVersion"] ?? "unknown";

        string otlpEndpoint =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? builder.Configuration["Observability:OtlpEndpoint"]
            ?? OtlpTransport.DefaultEndpoint;

        // Setting Endpoint explicitly (which OtlpTransport does, because it appends the per-signal
        // path) stops the SDK merging OTEL_EXPORTER_OTLP_HEADERS on our behalf, so the value is
        // read here and passed to every exporter by hand. Blank is fine for a collector on the
        // private network; SigNoz Cloud wants signoz-access-token=… here.
        string otlpHeaders =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS") ?? string.Empty;

        string roleName = role.ToString().ToLowerInvariant();

        Dictionary<string, object> resourceAttributes = new()
        {
            ["service.name"] = DiagnosticsConfig.ServiceName,
            ["service.version"] = serviceVersion,
            ["deployment.environment"] = deploymentEnvironment,
            ["host.name"] = Environment.MachineName,
            ["seo.role"] = roleName,
        };

        // Explicit W3C propagators so the trace that began in the freshness service — where a
        // job was decided to be dead — survives the hop through RabbitMQ and continues into the
        // removal and purge here. Explaining "why is this URL still in the sitemap" means
        // following one trace across two services.
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(
        [
            new TraceContextPropagator(),
            new BaggagePropagator(),
        ]));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("MassTransit", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", DiagnosticsConfig.ServiceName)
            .Enrich.WithProperty("service.version", serviceVersion)
            .Enrich.WithProperty("deployment.environment", deploymentEnvironment)
            .Enrich.WithProperty("seo.role", roleName)
            .Enrich.WithProperty("host.name", Environment.MachineName)
            .Enrich.With<ActivityEnricher>()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{seo.role}] {TraceId} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // SetMinimumLevel(Trace) disables the ILoggingBuilder pre-filter so every message
        // reaches both Serilog and OTel; each provider then applies its own level rules.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddSerilog(Log.Logger, dispose: true);

        // UseSerilogRequestLogging depends on DiagnosticContext, normally registered by
        // Host.UseSerilog(). We use AddSerilog() instead, to keep the OTel logging
        // provider alive, so it is registered by hand.
        //
        // Constructed explicitly rather than by type: its constructor takes a
        // Serilog.ILogger, which is not in the container, so letting DI activate it
        // fails container validation (and would fail at first request in production).
        Serilog.Extensions.Hosting.DiagnosticContext diagnosticContext = new(Log.Logger);
        builder.Services.AddSingleton(diagnosticContext);
        builder.Services.AddSingleton<Serilog.IDiagnosticContext>(diagnosticContext);

        ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(DiagnosticsConfig.ServiceName, serviceVersion: serviceVersion)
            .AddAttributes(resourceAttributes);

        builder.Logging.AddOpenTelemetry(opts =>
        {
            opts.IncludeFormattedMessage = true;
            opts.IncludeScopes = true;
            opts.ParseStateValues = true;
            opts.SetResourceBuilder(resourceBuilder);
            opts.AddOtlpExporter(o => OtlpTransport.Configure(o, otlpEndpoint, "LOGS", otlpHeaders));
        });

        // OTLP log volume valve. Information keeps Debug/Trace out of SigNoz, which is the right
        // default — but during an incident the missing detail is exactly the Debug line, and
        // widening it should not need a redeploy. Set OTEL_LOGS_MINIMUM_LEVEL=Debug and restart.
        // Console output is unaffected; only what ships to the collector changes.
        LogLevel otlpLogLevel =
            Enum.TryParse(
                Environment.GetEnvironmentVariable("OTEL_LOGS_MINIMUM_LEVEL"),
                ignoreCase: true,
                out LogLevel parsedLevel)
                ? parsedLevel
                : LogLevel.Information;

        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(null, otlpLogLevel);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(DiagnosticsConfig.ServiceName, serviceVersion: serviceVersion)
                .AddAttributes(resourceAttributes))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(opts =>
                {
                    opts.RecordException = true;

                    // Health and scrape traffic is high-volume and says nothing; sampling it
                    // would bury the rebuild traces we actually need.
                    opts.Filter = ctx =>
                        !ctx.Request.Path.StartsWithSegments("/metrics")
                        && !ctx.Request.Path.StartsWithSegments("/health");
                });

                tracing.AddHttpClientInstrumentation(opts => opts.RecordException = true);
                tracing.AddEntityFrameworkCoreInstrumentation();
                tracing.AddSource("MassTransit");
                tracing.AddSource(DiagnosticsConfig.ServiceName);
                // Copies the allow-listed baggage onto every span, so work this service does on
                // behalf of a CMS publish carries that page's id too. See BaggageSpanProcessor.
                tracing.AddProcessor(new BaggageSpanProcessor());

                tracing.AddOtlpExporter(
                    opts => OtlpTransport.Configure(opts, otlpEndpoint, "TRACES", otlpHeaders));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddProcessInstrumentation();
                metrics.AddRuntimeInstrumentation();
                metrics.AddMeter("MassTransit");
                metrics.AddMeter(DiagnosticsConfig.ServiceName);
                metrics.AddOtlpExporter(
                    opts => OtlpTransport.Configure(opts, otlpEndpoint, "METRICS", otlpHeaders));
                metrics.AddPrometheusExporter();
            });

        return builder;
    }
}
