namespace Kariyer.Seo.Worker.Common.Roles;

/// <summary>
/// Reads the role from <c>SERVICE_ROLE</c> (env, authoritative) falling back to the
/// <c>ServiceRole</c> configuration key.
///
/// An unrecognised value is a hard startup failure, and an ABSENT one is too — even though
/// PLAN §5 names <c>all</c> as the intended launch configuration. Defaulting to it would
/// mean a deployment that forgot to set the variable gets a second full rebuilder, and two
/// rebuilders staging <c>sitemap.xml</c> at once is exactly the failure the single-writer
/// rule exists to prevent. The default has to be stated per deployment, where the replica
/// count is stated too.
/// </summary>
public static class ServiceRoleResolver
{
    public const string EnvironmentVariable = "SERVICE_ROLE";
    public const string ConfigurationKey = "ServiceRole";

    public static ServiceRole Resolve(IConfiguration configuration)
    {
        string? raw =
            Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"No service role configured. Set the {EnvironmentVariable} environment variable "
                + $"or the '{ConfigurationKey}' configuration key to one of: {Describe()}.");
        }

        return Parse(raw);
    }

    /// <summary>Accepts the kebab-case spelling used in deployment manifests as well as the
    /// enum spelling.</summary>
    public static ServiceRole Parse(string raw)
    {
        string normalised = raw.Trim().Replace("-", string.Empty).Replace("_", string.Empty);

        return normalised.ToLowerInvariant() switch
        {
            "builder" => ServiceRole.Builder,
            "reactor" => ServiceRole.Reactor,
            "all" => ServiceRole.All,
            _ => throw new InvalidOperationException(
                $"Unknown service role '{raw}'. Expected one of: {Describe()}."),
        };
    }

    private static string Describe() => "builder, reactor, all";
}
