# kariyer-seo-service — PLAN

> A slim, single-purpose .NET 10 microservice that owns the **jobs SEO output surface**:
> sitemaps, prerender-cache invalidation, and (optionally) the Google Indexing API. It is
> the downstream consumer the freshness service was designed for — it subscribes to
> `JobExpired`/`JobResurrected`, rebuilds sitemaps from the live corpus, and emits its own
> SEO events over the shared contracts.
>
> This document is the canonical spec. Companion: `ARCHITECTURE.md` (code shape).
> Modeled 1:1 on `kariyer-freshness-service` conventions — same stack, same patterns.

---

## 0. The prime directive

**The database is the source of truth; R2 is a derived artifact.** Every sitemap can be
rebuilt from `company_job` + a small local state table at any moment. So the worst failure
this service can produce is **staleness bounded by the cron interval** — never data loss,
never a corrupt file served to a crawler. Two rules follow:

1. **Nothing is applied to R2 that isn't first durable in Postgres.** Inbound freshness
   events and the full-rebuild both write local state in a transaction; the R2 flush is a
   projection of that state and is always retryable.
2. **R2 writes are atomic.** A crawler never sees a half-written `sitemap.xml` — new files
   are staged and swapped, never edited in place.

This mirrors freshness's "precision over recall": there, a false expiry was unrecoverable;
here, a torn sitemap or a lost de-index is unrecoverable *for that crawl*, so we make both
impossible by construction rather than by luck.

---

## 1. Scope

**Goals**
- Generate the jobs sitemap set (`sitemap.xml` index + `sitemap-jobs-{n}.xml` +
  `sitemap-jobfilters.xml` + `sitemap-static.xml`) and `robots.txt`, upload to R2.
- React to `JobExpiredEvent` / `JobResurrectedEvent`: drop / re-admit the job URL and purge
  its prerender snapshot in Garnet.
- A full-corpus cron rebuild as the correctness backstop + facet-count refresh.
- Emit `Kariyer.Messaging.Contracts` **Seo** events for observability / downstream warmers.

**Non-goals** (explicit — do not creep)
- No withdrawal **detection** (freshness owns it). No writes to `company_job` — **read-only**.
- No HTTP **serving** of the files (Cloudflare serves them from R2).
- No JSON-LD / meta-tag emission (the SPA + prerender own the page HTML).
- No search UI, no analytics.

---

## 2. Stack (match freshness exactly — central package management)

| Concern | Choice |
|---|---|
| Runtime | **.NET 10**, framework-dependent |
| Messaging | **MassTransit 8.5.5** (+ `.RabbitMQ`, `.EntityFrameworkCore`) |
| Contracts | **`Kariyer.Messaging.Contracts`** (GitHub Packages NuGet), bumped to add `Seo/` |
| Persistence | **EF Core 10.0.3** + **Npgsql 10.0.0**, EF migrations |
| Object store | **AWS SDK for .NET S3** (`AWSSDK.S3`) pointed at the **R2** endpoint |
| Cache | **StackExchange.Redis** → **Garnet** (RESP-compatible) |
| XML | `System.Xml` `XmlWriter` (streaming), gzip via `System.IO.Compression` |
| Observability | Serilog + OpenTelemetry (OTLP + Prometheus) + AspNetCore health checks |
| Tests | xUnit, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, **Testcontainers** (Postgres + RabbitMq), `MassTransit.TestFramework` |

Add to the solution's `Directory.Packages.props` (versions pinned centrally, as freshness
does): `AWSSDK.S3`, `StackExchange.Redis`. Everything else already has a pinned version
in the house props you can copy.

---

## 3. DDD project layout (compiler-enforced boundaries)

```
Kariyer.Seo.Domain          ← pure. No I/O, no DbContext, no IConfiguration, no clock.
Kariyer.Seo.Worker          ← host, feature slices, ALL infrastructure.
Kariyer.Seo.Domain.Tests    ← golden XML fixtures, indexation rules, URL builders.
Kariyer.Seo.Worker.Tests    ← roles, options, debounce, slice unit tests.
Kariyer.Seo.IntegrationTests← host boot, Testcontainers, MassTransit harness, R2 stub.
```

`Kariyer.Seo.Domain` depends on **nothing** (BCL only). Time and I/O arrive as parameters
or through ports. That is what makes the sitemap XML, the chunking, and the indexation
threshold testable in milliseconds against saved fixtures instead of against a live DB/R2.

### Domain contents (`Kariyer.Seo.Domain`)
- **Value objects:** `SitemapUrl(Loc, LastMod?)`, `SitemapDocument`, `SitemapIndexDocument`.
- **URL builders** (the ONE place URLs are shaped — must match the SPA byte-for-byte):
  - `JobUrl.For(slug)` → `https://kariyerzamani.com/is-ilanlari/ilan/{slug}`
  - `FacetUrl.For(path)` → `https://kariyerzamani.com{path}`
- **`IndexationPolicy`** — pure decision mirroring the frontend `decideIndexation`:
  `axes == 1 → count ≥ 5`, `axes ≥ 2 → count ≥ 10`. Given a facet + live count → indexable?
- **`SitemapWriter`** — turns a stream of `SitemapUrl` into chunked XML (50k / 50MB caps),
  and the index. Deterministic output for golden tests.
- **`RobotsPolicy`** — produces `robots.txt` text.
- **Ports (interfaces, implemented in Worker):** `IJobCorpusReader`, `IFacetCountReader`,
  `ISitemapSink` (R2), `IPrerenderCache` (Garnet), `ISeoEventPublisher`, `TimeProvider`.

---

## 4. Vertical slices (`Kariyer.Seo.Worker/Features/`)

`Features/<Capability>/<UseCase>/` — one operation, one folder. Endpoints discovered by
assembly scan (`IEndpoint`), like freshness.

| Slice | Role | Trigger | Does |
|---|---|---|---|
| `Features/Sitemaps/RebuildAll` | `builder` | cron (`PeriodicWorker`, 30–60m) + on-demand | read live corpus + facet counts → build all sitemaps → **stage+swap** to R2 → emit `SitemapRebuiltEvent` |
| `Features/Sitemaps/ApplyJobExpired` | `reactor` | `JobExpiredEvent` consumer | set `seo_url_state` = removed (txn) → purge Garnet keys → (opt) Indexing `URL_DELETED` → mark sitemap-jobs dirty |
| `Features/Sitemaps/ApplyJobResurrected` | `reactor` | `JobResurrectedEvent` consumer | set state = live (txn) → purge Garnet → (opt) `URL_UPDATED` → mark dirty |
| `Features/Sitemaps/FlushDirty` | `reactor` | debounced timer (e.g. 30s coalesce) | if dirty, re-project `sitemap-jobs` from `seo_url_state` → stage+swap to R2 |
| `Features/Indexing/SubmitIndexing` *(optional)* | `reactor` | invoked by the two consumers | Google Indexing API call; emit `JobUrlIndexingSubmittedEvent` |
| `Features/Diagnostics/GetSitemapStatus` | any | `GET /diag/sitemap` | last rebuild time, per-file URL counts, dirty flag |
| `Features/Diagnostics/RebuildNow` | `builder` | `POST /diag/rebuild` (guarded) | manual full rebuild |

**Role is a registration decision, not a folder axis** (copy freshness's `RolePlan`).

---

## 5. Roles (one binary, `SERVICE_ROLE`) — blast-radius split

| Role | Runs | Replicas |
|---|---|---|
| `builder` | cron full-rebuild + R2 write | **1** (holds the R2 write) |
| `reactor` | freshness-event consumers + dirty flush + Garnet purge | 1 (can scale, but keep 1 — writes shared sitemap state) |
| `all` | everything — local dev + acceptable launch default | 1 |

Launch as **`SERVICE_ROLE=all`, 1 replica**. The role seam exists so `builder` can be split
out later without touching a slice. `RolePlan.WritesToR2` is a named, tested property; the
builder logs a startup warning that it holds the R2 write (mirrors freshness's applier).

> Why single-writer to R2: two builders staging `sitemap.xml` concurrently could swap in
> each other's half-finished index. Same reasoning as freshness's single applier.

---

## 6. Data losslessness — the mechanics

### 6.1 Inbound (freshness events) — durable before applied
The two consumers are **idempotent** and write to Postgres in a transaction that includes
the **MassTransit inbox** (dedup) + the local state row. A redelivery is a no-op; a broker
double-send can't double-remove. Concretely:

```
JobExpiredEvent(jobUid, slug) →
  BEGIN
    upsert seo_url_state(jobUid) { slug, status=removed, last_modified=now, dirty=true }
    (inbox dedup row via MassTransit EF inbox)
  COMMIT
  → enqueue Garnet purge + mark sitemap-jobs dirty
```
If the process dies after COMMIT but before the Garnet purge, the next `FlushDirty`/redelivery
repeats the purge (idempotent `DEL`). **Nothing is lost** because the DB row is the truth.

### 6.2 Outbound (our own SEO events) — transactional outbox
Register `AddEntityFrameworkOutbox<SeoDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })`
exactly as freshness does. Emitting `SitemapRebuiltEvent` (or `FacetIndexabilityChanged`)
is written in the **same commit** as the state change that produced it, so a broker outage
never loses an event and a crash never emits one without the state behind it.

### 6.3 R2 atomic swap
`SitemapSink` writes each file to a **staging key** (`_staging/sitemap-jobs-1.xml.gz`), then
performs the swap only once every file of the set is uploaded — either by copying staging →
live keys, or by writing to a versioned prefix and updating a single `sitemap.xml` pointer
last. A crawler fetching mid-rebuild always gets a **consistent, complete** set.

> **Revised in implementation.** The plan called for gzipping the files (Google accepts
> `.xml.gz`), and `Seo:R2:Compress` still does exactly that. It now defaults to **false**,
> because Cloudflare fronts the bucket and re-compresses what it is given — the edge should
> own content encoding when there is an edge. See `docs/DEPLOYMENT.md` §4.

### 6.4 Reconstruction
On boot, or if `seo_url_state` is ever suspected stale, `RebuildAll` re-derives everything
from `company_job` — the local state is a cache, not a ledger. This is the ultimate
loss-proof guarantee: **truth is always one query away.**

---

## 7. Performance

- **Facet counts in one pass.** A single set of read-only aggregate queries —
  `COUNT(*) … GROUP BY province`, `… GROUP BY industry`, `… GROUP BY position`,
  `… GROUP BY working_type`/`working_prefs`, and the curated `city×sector` combos — over
  live jobs (`status='approved' AND is_active=true`). Materialize once per rebuild; map onto
  the manifest. **Never one query per facet.** Index `company_job(status, is_active)` and the
  grouped columns.
- **Streaming XML.** `XmlWriter` straight into a gzip stream into an S3 multipart upload — no
  giant in-memory string. Chunk at 50k URLs.
- **Debounced incremental flush.** A burst of expiries coalesces (System.Threading.Channels +
  a 30s timer) into **one** `sitemap-jobs` re-projection, not N. Full facet recompute stays on
  the cron; expiries only touch `sitemap-jobs`.
- **Read replica** for the heavy aggregates if one exists (config-selectable connection), so
  a rebuild never contends with the API's primary.
- **Conditional R2 writes** — skip upload if the new file's checksum equals the last
  (`seo_rebuild_log.checksum`); most cron ticks change nothing.
- Cloudflare caches the served files; set sane `Cache-Control` on the R2 objects.

---

## 8. Persistence (its own schema `seo` in the jobs Postgres)

Co-locate with `company_job` (same DB) so the read model + outbox share one connection and
one transaction — exactly how `FreshnessDbContext` maps `CompanyJobReadModel` (read-only)
alongside its own tables.

`SeoDbContext`:
- **`CompanyJobReadModel`** — read-only mapping of `company_job` (uid, slug_url, status,
  is_active, modified_on, province, town, industry, position, working_type, working_prefs).
  `.ToView`/no-tracking; never written.
- **`seo_url_state`** (`job_uid` PK, `slug`, `status` live|removed, `last_modified`, `dirty`,
  `updated_at`) — incremental job-URL projection between full rebuilds.
- **`seo_facet_state`** (`facet_path` PK, `axes`, `job_count`, `indexable`, `last_modified`) —
  last computed facet indexability (drives `FacetIndexabilityChangedEvent`).
- **`seo_rebuild_log`** (`id`, `file`, `url_count`, `checksum`, `generated_at`) — audit + the
  conditional-write short-circuit.
- **MassTransit** `InboxState` / `OutboxState` / `OutboxMessage` (EF outbox tables).

`DatabaseMigrator` runs EF migrations at startup (copy freshness's). Ship `docs/schema.sql`
and test against it with Testcontainers (their `deploy/smoke/company_job_standin.sql` shows
the read-model shape to stub).

---

## 9. Contracts it emits — add `Kariyer.Messaging.Contracts/Seo/`

Bump the shared package to **1.1.0**. New records (records with `init`, `MessageId` first —
match the Account/Freshness convention). Exchanges follow `<service>.<entity>.<action>`:

```csharp
namespace Kariyer.Messaging.Contracts.Seo;

/// Emitted after a sitemap file set is (re)built and swapped live. For dashboards,
/// prerender warmers, and "sitemap freshness" alerting. Exchange: seo.sitemap.rebuilt
public record SitemapRebuiltEvent
{
    public string MessageId { get; init; } = string.Empty;
    public string SitemapType { get; init; } = string.Empty;   // jobs | jobfilters | static | index
    public int UrlCount { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
}

/// Emitted when a facet crosses the indexability threshold (in or out). Lets a prerender
/// warmer pre-render newly-indexable facets. Exchange: seo.facet.indexability_changed
public record FacetIndexabilityChangedEvent
{
    public string MessageId { get; init; } = string.Empty;
    public string FacetPath { get; init; } = string.Empty;     // /is-ilanlari/istanbul/yazilim
    public bool Indexable { get; init; }
    public int JobCount { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
}

/// (Optional) Audit of a Google Indexing API submission. Exchange: seo.indexing.submitted
public record JobUrlIndexingSubmittedEvent
{
    public string MessageId { get; init; } = string.Empty;
    public string JobUid { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;        // URL_UPDATED | URL_DELETED
    public bool Success { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}
```

MassTransit wiring (copy freshness's `MessagingExtensions`): `SetEntityName` on each message
to its fanout exchange (`seo.sitemap.rebuilt`, `seo.facet.indexability_changed`,
`seo.indexing.submitted`); consume `JobExpiredEvent`/`JobResurrectedEvent` on the freshness
fanout exchanges (`freshness.job.expired`, `freshness.job.resurrected`) via a **dedicated
receive endpoint** (own queue, e.g. `seo.freshness.consumer`); `UseEntityFrameworkOutbox`,
exponential retry, kill-switch — same policy block. Keep `Events:RawJson=false` (all .NET).

---

## 10. Config (`appsettings.json` + env, `IOptions` with validation)
```jsonc
{
  "Seo": {
    "SiteUrl": "https://kariyerzamani.com",
    "CronInterval": "00:45:00",
    "DebounceWindow": "00:00:30",
    "Thresholds": { "SingleAxis": 5, "Combo": 10 },
    "FacetManifest": { "Url": "https://kariyerzamani.com/seo/facet-manifest.json" },
    "StaticPaths": ["/", "/sirketler", "/cv", "/isveren", "/hakkimizda", "/iletisim", "/sıksorulansorular"],
    "R2": { "Endpoint": "", "Bucket": "", "Prefix": "", "AccessKey": "", "SecretKey": "" }
  },
  "Rabbit": {
    "JobExpiredExchange": "freshness.job.expired",
    "JobResurrectedExchange": "freshness.job.resurrected",
    "SitemapRebuiltExchange": "seo.sitemap.rebuilt",
    "FreshnessConsumerQueue": "seo.freshness.consumer",
    "PrefetchCount": 16, "ConcurrentMessageLimit": 8
  },
  "Persistence": { "ConnectionString": "", "ReadReplicaConnectionString": "" },
  "Garnet": { "ConnectionString": "" },
  "Events": { "RawJson": false }
}
```
Validate with DataAnnotations like `RabbitOptions`. The facet manifest can be watched/cached
(`IOptionsMonitor`-style) if you fetch it locally.

---

## 11. Endpoints (assembly-scanned `IEndpoint`)
| Path | Purpose |
|---|---|
| `/health` | Liveness — no dependencies |
| `/health/ready` | Readiness — includes Postgres |
| `/metrics` | Prometheus |
| `GET /diag/sitemap` | last rebuild, per-file counts, dirty flag |
| `POST /diag/rebuild` | manual full rebuild (guarded) |

## 12. Serving & infra (note in DEPLOYMENT.md)
- **Cloudflare rule:** `/sitemap*.xml` and `/robots.txt` → the R2 bucket/prefix.
- **Search Console:** submit `https://kariyerzamani.com/sitemap.xml` **once** (Google
  re-fetches; the ping endpoint is deprecated — `<lastmod>` is the freshness signal).
- **Optional Bing IndexNow** ping on expiry/resurrection.
- **Deploy:** single replica; Dockerfile mirrors freshness's (multi-stage, .NET 10,
  GitHub-Packages secrets for the contracts restore, framework-dependent publish).

---

## 13. Tests (match the freshness bar)
- **`Domain.Tests`** (ms, no I/O): golden XML fixtures for `sitemap-jobs`/`jobfilters`/index
  (chunking at 50k, gzip determinism), `IndexationPolicy` thresholds, `JobUrl`/`FacetUrl`
  builders, `RobotsPolicy`.
- **`Worker.Tests`**: options validation, `RolePlan` (asserts `builder` writes R2 / no
  `company_job` write), debounce/coalesce logic (with `FakeTimeProvider`).
- **`IntegrationTests`** (Testcontainers Postgres + RabbitMq, MassTransit harness, an R2
  stub such as MinIO or a fake `ISitemapSink`):
  - `JobExpiredEvent` → `seo_url_state` flips to removed, Garnet key purged, `sitemap-jobs`
    re-projected, **redelivery is a no-op** (inbox dedup).
  - Full `RebuildAll` over a `company_job` stand-in → exact sitemap set + `SitemapRebuiltEvent`
    published **via the outbox** (assert by receipt on a probe consumer).
  - Atomic swap: a reader during a rebuild never sees a partial index.

---

## 14. Failure modes (and why each is safe)
| Failure | Behavior |
|---|---|
| Broker down while emitting | Outbox holds the event; delivered on reconnect. No loss. |
| Process dies mid-rebuild | Staging keys never swapped in; live set unchanged. Next cron/rebuild redoes it. |
| Garnet down on purge | Purge retried on next flush/redelivery; TTL is the backstop (prerender cache has its own TTL). |
| DB read-replica lag | Sitemap is at most `lag` stale; cron re-runs. Never wrong, only late. |
| Duplicate freshness event | Inbox dedup → no-op. |
| `company_job` schema drift | Testcontainers golden tests fail in CI against `schema.sql`. |

---

## 15. Build order (milestones)
1. **Skeleton** — solution, Domain + Worker projects, `Directory.Packages.props` (+ AWSSDK.S3,
   StackExchange.Redis), host, health/metrics, `RolePlan`, config + validation. Boots as `all`.
2. **Domain** — `SitemapWriter`, `JobUrl`/`FacetUrl`, `IndexationPolicy`, `RobotsPolicy` +
   golden tests. No I/O.
3. **Persistence** — `SeoDbContext` (read model + state + outbox), migrations, `DatabaseMigrator`,
   Testcontainers.
4. **RebuildAll** — corpus reader + facet-count aggregates + manifest fetch → build → R2
   stage+swap → `SitemapRebuiltEvent`. Cron via `PeriodicWorker`.
5. **Consumers** — `ApplyJobExpired`/`ApplyJobResurrected` + Garnet purge + `FlushDirty`
   debounce + inbox dedup.
6. **Infra** — Dockerfile, CF `/sitemap*.xml`→R2 rule, robots.txt, Search Console submit.
7. **Optional** — Google Indexing API slice + `JobUrlIndexingSubmittedEvent`.

---

## 16. Dependencies on other repos
- **`kariyer-zamani-web`** → emits **`public/seo/facet-manifest.json`** (candidate indexable
  facet paths + `axes`). §4 `RebuildAll` reads it for `sitemap-jobfilters.xml`. **Hard
  dependency** — without it the filter sitemap can't match Part C.
- **`kariyer-messaging-contracts`** → add the `Seo/` folder (§9), bump to 1.1.0, publish to
  GitHub Packages. The service references it centrally like freshness does.
- **`kariyer-freshness-service`** → no change; this service consumes its existing events.
- **Cloudflare** → the `/sitemap*.xml` + `/robots.txt` → R2 rule.

## Canonical URL formats (must match the SPA exactly)
- Job detail: `https://kariyerzamani.com/is-ilanlari/ilan/{slug_url}`
- Facet pages: from `facet-manifest.json` (already full `/is-ilanlari/...` paths)
- Prerender Garnet keys to purge on expiry (all variants — old URLs still 301):
  `prerender:https://kariyerzamani.com/is-ilanlari/ilan/{slug}`,
  `prerender:https://kariyerzamani.com/ilanlar/slug/{slug}`,
  `prerender:https://kariyerzamani.com/jobs/slug/{slug}`
