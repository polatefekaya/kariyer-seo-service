using Kariyer.Seo.Worker.Common.Roles;
using Microsoft.Extensions.Configuration;

namespace Kariyer.Seo.Worker.Tests.Roles;

public sealed class ServiceRoleResolverTests
{
    [Theory]
    [InlineData("builder", ServiceRole.Builder)]
    [InlineData("BUILDER", ServiceRole.Builder)]
    [InlineData(" Reactor ", ServiceRole.Reactor)]
    [InlineData("all", ServiceRole.All)]
    public void ParsesTheSpellingsDeploymentManifestsUse(string raw, ServiceRole expected) =>
        Assert.Equal(expected, ServiceRoleResolver.Parse(raw));

    [Fact]
    public void AnUnknownRoleIsAHardFailure()
    {
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => ServiceRoleResolver.Parse("applier"));

        // The message must name the valid values: this fires at boot, in a container, where
        // the only diagnostic anyone gets is the log line.
        Assert.Contains("builder, reactor, all", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentRoleIsAHardFailure()
    {
        // Not defaulted to 'all', even though PLAN §5 names it as the launch configuration.
        // Defaulting would mean a deployment that forgot the variable silently gets a SECOND
        // full rebuilder — and two replicas staging sitemap.xml concurrently can swap in each
        // other's half-finished index, which no log or metric would ever show.
        IConfiguration empty = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => ServiceRoleResolver.Resolve(empty));
    }

    [Fact]
    public void ConfigurationIsUsedWhenTheEnvironmentVariableIsAbsent()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceRoleResolver.ConfigurationKey] = "reactor",
            })
            .Build();

        Assert.Equal(ServiceRole.Reactor, ServiceRoleResolver.Resolve(configuration));
    }
}
