using Kariyer.Seo.Worker.Common.Roles;

namespace Kariyer.Seo.Worker.Tests.Roles;

public sealed class RolePlanTests
{
    [Fact]
    public void NoRoleEverWritesCompanyJob()
    {
        // The single most important thing this service does NOT do (PLAN §1).
        //
        // company_job belongs to the Node application, and the freshness service owns the one
        // legitimate write to it behind a mass-expiry safety valve that only works when one
        // process sees a whole batch. A sitemap builder with a write path to that table would
        // be a second author of the same rows with none of that machinery — and its mistakes
        // would remove live postings employers are paying for.
        //
        // Asserted here rather than trusted, so adding such a path breaks a test instead of a
        // catalogue.
        foreach (ServiceRole role in Enum.GetValues<ServiceRole>())
        {
            Assert.False(
                RolePlan.For(role).WritesToCompanyJob,
                $"Role {role} claims a write to company_job. This service is read-only on that "
                + "table; the freshness service owns the write.");
        }
    }

    [Fact]
    public void EveryRoleThatBuildsASitemapIsAnR2Writer()
    {
        // WritesToR2 is the dangerous capability here, and it must not be possible for a role
        // to produce a sitemap without being recognised as holding it — that is what the
        // startup warning and the one-replica rule key off.
        foreach (ServiceRole role in Enum.GetValues<ServiceRole>())
        {
            RolePlan plan = RolePlan.For(role);

            if (plan.RunsFullRebuild || plan.RunsDirtyFlush)
            {
                Assert.True(plan.WritesToR2, $"Role {role} produces sitemaps but is not an R2 writer.");
            }
        }
    }

    [Fact]
    public void BuilderRunsTheCronAndNothingElse()
    {
        RolePlan plan = RolePlan.For(ServiceRole.Builder);

        Assert.True(plan.RunsFullRebuild);
        Assert.False(plan.ConsumesFreshnessEvents);
        Assert.False(plan.RunsDirtyFlush);

        // No consumers means no purges, so it must not be handed a Garnet connection.
        Assert.False(plan.NeedsPrerenderCache);
        Assert.True(plan.NeedsFacetManifest);
        Assert.True(plan.WritesToR2);
    }

    [Fact]
    public void ReactorConsumesAndFlushesButNeverRebuilds()
    {
        RolePlan plan = RolePlan.For(ServiceRole.Reactor);

        Assert.False(plan.RunsFullRebuild);
        Assert.True(plan.ConsumesFreshnessEvents);
        Assert.True(plan.RunsDirtyFlush);

        Assert.True(plan.NeedsPrerenderCache);

        // It never builds sitemap-jobfilters.xml, so it has no business fetching the manifest
        // from the web app — one fewer cross-repo dependency on the pod that has to react
        // promptly to freshness events.
        Assert.False(plan.NeedsFacetManifest);

        // It still writes R2, via the incremental flush. This is the reason 'reactor' is also
        // pinned to one replica.
        Assert.True(plan.WritesToR2);
    }

    [Fact]
    public void AllRunsEverything()
    {
        // Unlike the freshness service, 'all' is the intended LAUNCH configuration here
        // (PLAN §5) — both other roles write to R2 anyway, so splitting them buys nothing
        // until one needs to scale independently.
        RolePlan plan = RolePlan.For(ServiceRole.All);

        Assert.True(plan.RunsFullRebuild);
        Assert.True(plan.ConsumesFreshnessEvents);
        Assert.True(plan.RunsDirtyFlush);
        Assert.True(plan.WritesToR2);
    }

    [Fact]
    public void EveryRoleNeedsStateAndTheBus()
    {
        foreach (ServiceRole role in Enum.GetValues<ServiceRole>())
        {
            RolePlan plan = RolePlan.For(role);
            Assert.True(plan.NeedsDatabase);
            Assert.True(plan.NeedsBus);
        }
    }

    [Fact]
    public void EveryRoleDoesSomething()
    {
        // Guards against a role being added to the enum without being wired up — which would
        // deploy a replica that passes every health check and quietly does nothing, on a
        // service whose only failure symptom is "the sitemap stopped changing".
        foreach (ServiceRole role in Enum.GetValues<ServiceRole>())
        {
            RolePlan plan = RolePlan.For(role);

            Assert.True(
                plan.RunsFullRebuild || plan.ConsumesFreshnessEvents || plan.RunsDirtyFlush,
                $"Role {role} has no responsibilities.");
        }
    }
}
