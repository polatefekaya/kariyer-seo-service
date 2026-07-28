using Kariyer.Messaging.Contracts.Cms;
using Kariyer.Messaging.Contracts.Freshness;
using Kariyer.Messaging.Contracts.Seo;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Roles;
using Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobExpired;
using Kariyer.Seo.Worker.Features.Pages.ApplyCmsPagePublished;
using Kariyer.Seo.Worker.Features.Pages.ApplyCmsPageUnpublished;
using Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobResurrected;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Common.Messaging;

/// <summary>
/// Wires MassTransit for the role this process is running.
///
/// Only the consumers a role needs are registered, so a builder pod never binds the
/// freshness queue.
/// </summary>
public static class MessagingExtensions
{
    public static IServiceCollection AddMessaging(
        this IServiceCollection services, RolePlan plan, string rabbitConnection)
    {
        services.AddMassTransit(bus =>
        {
            // The outbox is registered for EVERY role, including one that consumes nothing.
            //
            // The builder publishes SitemapRebuiltEvent in the same commit as the
            // seo_rebuild_log row that records the swap. Without the outbox, a broker outage
            // in the gap leaves a sitemap that changed and an estate that was never told —
            // and because the log row committed, the next rebuild's checksum comparison finds
            // no difference and never re-emits. The event would be lost permanently, with
            // nothing in the system aware of it.
            bus.AddEntityFrameworkOutbox<SeoDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
            });

            if (plan.ConsumesFreshnessEvents)
            {
                bus.AddConsumer<JobExpiredConsumer>();
                bus.AddConsumer<JobResurrectedConsumer>();
            }

            if (plan.ConsumesCmsEvents)
            {
                bus.AddConsumer<CmsPagePublishedConsumer>();
                bus.AddConsumer<CmsPageUnpublishedConsumer>();
            }

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(rabbitConnection));

                // Bus-wide, so it covers both queues and anything added later. See
                // BaggageRestoration: MassTransit propagates the traceparent but not baggage, so
                // the page id a CMS publish put on the message is lifted back here by hand.
                cfg.UseCmsBaggage();

                RabbitOptions rabbit = context.GetRequiredService<IOptions<RabbitOptions>>().Value;

                // House convention: <service>.<entity>.<action> fanout exchanges.
                //
                // The two INBOUND names must match what the freshness service publishes to,
                // byte for byte. They are a contract with another repository, not a local
                // preference: a typo does not fail, it just silently binds an empty exchange
                // and this service stops hearing about expiries while looking perfectly
                // healthy.
                cfg.Message<JobExpiredEvent>(m => m.SetEntityName(rabbit.JobExpiredExchange));
                cfg.Message<JobResurrectedEvent>(m => m.SetEntityName(rabbit.JobResurrectedExchange));

                cfg.Message<CmsPagePublishedEvent>(
                    m => m.SetEntityName(rabbit.CmsPagePublishedExchange));
                cfg.Message<CmsPageUnpublishedEvent>(
                    m => m.SetEntityName(rabbit.CmsPageUnpublishedExchange));

                cfg.Message<SitemapRebuiltEvent>(m => m.SetEntityName(rabbit.SitemapRebuiltExchange));
                cfg.Message<FacetIndexabilityChangedEvent>(
                    m => m.SetEntityName(rabbit.FacetIndexabilityChangedExchange));
                cfg.Message<JobUrlIndexingSubmittedEvent>(
                    m => m.SetEntityName(rabbit.IndexingSubmittedExchange));

                if (context.GetRequiredService<IOptions<EventsOptions>>().Value.RawJson)
                {
                    // Only needed if a non-.NET consumer subscribes. Currently off — and note
                    // it would also strip the envelope the INBOUND freshness events rely on,
                    // so turning it on is not a one-sided decision.
                    cfg.UseRawJsonSerializer(RawSerializerOptions.AnyMessageType);
                }

                if (plan.ConsumesFreshnessEvents)
                {
                    // ONE queue for both consumers, bound to both fanout exchanges.
                    //
                    // One rather than two because expiry and resurrection are opposite
                    // operations on the same seo_url_state row. On separate queues they would
                    // be delivered concurrently and could interleave — a resurrection landing
                    // before the expiry that preceded it leaves the job removed from the
                    // sitemap when it is live. One queue with a bounded concurrency keeps
                    // per-job ordering as the broker delivered it.
                    cfg.ReceiveEndpoint(rabbit.FreshnessConsumerQueue, endpoint =>
                    {
                        endpoint.PrefetchCount = rabbit.PrefetchCount;
                        endpoint.ConcurrentMessageLimit = rabbit.ConcurrentMessageLimit;

                        // The inbox. This is what makes redelivery a genuine no-op rather
                        // than something that happens to be harmless because both consumers
                        // are currently written idempotently (PLAN §6.1).
                        endpoint.UseEntityFrameworkOutbox<SeoDbContext>(context);

                        ApplyResilience(endpoint);

                        endpoint.ConfigureConsumer<JobExpiredConsumer>(context);
                        endpoint.ConfigureConsumer<JobResurrectedConsumer>(context);
                    });
                }

                if (plan.ConsumesCmsEvents)
                {
                    // A SECOND queue, not more consumers on the freshness one. The two
                    // sources are independent: a CMS outage must not stall job expiries, and
                    // sharing a queue would let one slow consumer back the other up behind it.
                    //
                    // No EF inbox here, unlike the freshness endpoint. These consumers write
                    // NO local state — cms.seo_page is the truth and the flush re-reads it —
                    // so a redelivery costs a repeated Garnet DEL and a repeated signal, both
                    // idempotent by nature. An inbox would add a table write per message to
                    // deduplicate operations that do not need it.
                    cfg.ReceiveEndpoint(rabbit.CmsConsumerQueue, endpoint =>
                    {
                        endpoint.PrefetchCount = rabbit.PrefetchCount;
                        endpoint.ConcurrentMessageLimit = rabbit.ConcurrentMessageLimit;

                        ApplyResilience(endpoint);

                        endpoint.ConfigureConsumer<CmsPagePublishedConsumer>(context);
                        endpoint.ConfigureConsumer<CmsPageUnpublishedConsumer>(context);
                    });
                }

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>
    /// Retry and kill-switch policy.
    ///
    /// Note what does NOT reach it: a Garnet purge failure. The cache adapter logs, counts
    /// and returns rather than throwing, because the database row is already the truth and
    /// retrying the whole message to re-attempt a cache DELETE would replay a committed
    /// removal for no gain. So anything arriving here is a genuine infrastructure fault —
    /// Postgres unreachable, the broker hiccuping — or a bug, and both deserve a retry and
    /// then visibility.
    ///
    /// The kill switch stops us consuming when OUR side is broken, which for this service
    /// means the alternative is silently marking jobs removed without ever flushing.
    /// </summary>
    private static void ApplyResilience(IRabbitMqReceiveEndpointConfigurator endpoint)
    {
        endpoint.UseMessageRetry(retry => retry.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromSeconds(5),
            maxInterval: TimeSpan.FromSeconds(60),
            intervalDelta: TimeSpan.FromSeconds(5)));

        endpoint.UseKillSwitch(killSwitch => killSwitch
            .SetActivationThreshold(10)
            .SetTripThreshold(0.5)
            .SetRestartTimeout(TimeSpan.FromMinutes(1)));
    }
}
