namespace Kariyer.Seo.Worker.Common.Web;

/// <summary>
/// The <c>--healthcheck</c> entry point used by the container HEALTHCHECK.
///
/// It exists because the .NET runtime images ship without curl or wget, and adding either
/// just to probe a local endpoint would mean carrying a shell and a network utility in
/// every image for the rest of the service's life. Probing from inside the app costs
/// nothing extra and cannot drift from the port the app actually listens on.
///
/// It deliberately checks LIVENESS only. Readiness depends on Postgres and RabbitMQ, and a
/// container runtime that fails a health check will RESTART the container — so wiring
/// readiness here would turn a brief database blip into a fleet-wide restart storm.
/// Readiness belongs to the orchestrator, which can take a pod out of rotation without
/// killing it.
/// </summary>
public static class HealthCheckCommand
{
    public const string Argument = "--healthcheck";

    public static bool ShouldRun(string[] args) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns 0 when the process is live, 1 otherwise.</summary>
    public static async Task<int> RunAsync()
    {
        string port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            HttpResponseMessage response = await client.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"healthcheck failed: {ex.Message}");
            return 1;
        }
    }
}
