# kariyer-seo-service

A slim, single-purpose .NET 10 microservice that owns the **jobs SEO output surface**:
sitemaps, prerender-cache invalidation, and (optionally) the Google Indexing API.

It is the downstream consumer the freshness service was designed for — it subscribes to
`JobExpiredEvent` / `JobResurrectedEvent`, rebuilds the sitemap set from the live corpus, and
emits its own SEO events over the shared contracts. It does the same for
`kariyer-cms-service`'s landing pages.

```
company_job   ──read-only──┐
cms.seo_page  ──read-only──┴─▶ kariyer-seo-service ──stage+swap──▶ R2 ──Cloudflare──▶ Googlebot
                                     ▲    │
        freshness.job.expired ───────┤    └──▶ Garnet purge  +  seo.sitemap.rebuilt
        cms.page.published ──────────┘
```

## Documents

| | |
|---|---|
| [`docs/PLAN.md`](docs/PLAN.md) | The canonical spec. Authoritative. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Code shape, and where the implementation departs from the plan. |
| [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) | Running it, the Cloudflare rules, the runbook. |
| [`docs/schema.sql`](docs/schema.sql) | Generated from the EF model and pinned by a test. |

## The prime directive

**The database is the source of truth; R2 is a derived artifact.** Every sitemap is
reconstructable from `company_job` at any moment, so the worst failure this service can
produce is *staleness bounded by the cron interval* — never data loss, never a torn file
served to a crawler.

Two rules follow, and both are structural rather than aspirational:

- Nothing reaches R2 that is not first durable in Postgres.
- R2 writes are staged and swapped, never edited in place. `ISitemapSink` has no
  "upload this file" method, because such a method makes a torn set the default outcome.

## The thing to keep in mind

**This service has no user-facing failure mode.** Nothing 500s when the sitemap is wrong. A
rebuild that has been failing for a week looks exactly like a quiet catalogue.

That shapes almost everything: startup validation is aggressive because runtime cannot notice
a blank bucket; the metrics are chosen so *silence* is alertable; the domain is pure so a
wrong `<lastmod>` is caught by a millisecond-fast fixture test rather than by a traffic graph
six weeks later.

## Quick start

```bash
# 1. Publish the contracts package (Seo/ + Cms/, ≥ 1.2.0)
cd ../kariyer-messaging-contracts && ./prepare.sh && ./publish.sh

# 2. Build and test
cd ../kariyer-seo-service
export GITHUB_USER=… GITHUB_TOKEN=…
dotnet test Kariyer.Seo.slnx          # integration tests need a Docker daemon

# 3. Run locally (appsettings.Development.json switches off R2 and Garnet)
dotnet run --project src/Kariyer.Seo.Worker --launch-profile all
```

| Endpoint | |
|---|---|
| `GET /health` | Liveness — no dependencies |
| `GET /health/ready` | Readiness — Postgres + RabbitMQ |
| `GET /metrics` | Prometheus |
| `GET /api/seo/diag/sitemap` | Last rebuild, per-file URL counts, dirty backlog |
| `POST /api/seo/diag/rebuild` | Manual full rebuild (builder roles only) |

## Layout

```
src/Kariyer.Seo.Domain      pure — no I/O, no DbContext, no clock. Zero PackageReferences.
src/Kariyer.Seo.Worker      host, feature slices, all infrastructure.
tests/…Domain.Tests         golden XML fixtures, thresholds, URL builders, robots.
tests/…Worker.Tests         roles, options validation, debounce, schema pinning.
tests/…IntegrationTests     Testcontainers Postgres + RabbitMQ, MassTransit harness, R2 stub.
```

`Kariyer.Seo.Domain.csproj` contains no package reference at all. That emptiness is the
boundary enforcement — not a convention anyone has to remember.

## What it does not do

- **Write `company_job`.** Ever. `RolePlan.WritesToCompanyJob` is a property that is always
  false, asserted for every role and again against the real table in the integration suite.
- **Detect withdrawals.** `kariyer-freshness-service` owns that.
- **Serve the sitemaps.** Cloudflare serves them from R2. No route here returns one.
- **Hardcode the facet list.** It comes from `kariyer-zamani-web`'s `facet-manifest.json`,
  validated but never invented.

## Dependencies on other repos

- **`kariyer-messaging-contracts`** — `Seo/` (published) + `Cms/` (consumed), ≥ 1.2.0.
- **`kariyer-zamani-web`** — publishes `public/seo/facet-manifest.json`. Each entry carries
  the backend values its facet selects, not only a path: the sector slug `yazilim` maps to
  `department IN ('Bilişim', 'Bilişim - İnternet')` and shares no characters with them, so a
  path alone cannot be counted. See `docs/ARCHITECTURE.md` §5.
- **`kariyer-freshness-service`** — no change; this service consumes its existing events.
- **`kariyer-cms-service`** — owns `cms.seo_page` in the same Postgres. This service reads it
  read-only to build `sitemap-pages.xml`, and consumes `cms.page.published` /
  `cms.page.unpublished` so a publish reaches the sitemap in ~30s rather than up to 45
  minutes. See `docs/ARCHITECTURE.md` §10.
- **Cloudflare** — the `/sitemap*.xml` + `/robots.txt` → R2 rules.
