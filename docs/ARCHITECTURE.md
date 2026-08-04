# kariyer-seo-service — ARCHITECTURE

> Code shape. The canonical spec is [`PLAN.md`](./PLAN.md); this document says how the plan
> is realised and, where the two differ, why.

---

## 0. The one thing to keep in mind

**This service has no user-facing failure mode.** Nothing 500s when the sitemap is wrong. No
request is slow. No customer complains within the hour. A rebuild that has been failing for a
week and a rebuild that had nothing to do look identical from outside the process.

Almost every design decision below follows from that. Startup validation is aggressive
because runtime cannot notice a blank bucket. Metrics are chosen so that *silence* is
alertable. Golden tests pin the exact bytes because no downstream consumer validates them.
The domain is kept pure because a wrong `<lastmod>` is invisible until traffic moves weeks
later, and milliseconds-fast fixture tests are the only place it gets caught.

---

## 1. Projects

```
Kariyer.Seo.Domain           ← pure. No I/O, no DbContext, no IConfiguration, no clock.
Kariyer.Seo.Worker           ← host, feature slices, ALL infrastructure.
Kariyer.Seo.Domain.Tests     ← golden XML fixtures, thresholds, URL builders, robots.
Kariyer.Seo.Worker.Tests     ← roles, options validation, debounce, schema pinning.
Kariyer.Seo.IntegrationTests ← Testcontainers Postgres + RabbitMQ, MassTransit harness, R2 stub.
```

`Kariyer.Seo.Domain.csproj` contains **no `PackageReference` at all**, and that emptiness is
the enforcement mechanism — not a convention anyone has to remember. `System.Xml` and
`System.IO.Compression` are used and are fine: both ship in the shared framework, both operate
on a `Stream` the caller supplies, and neither opens a socket or touches the filesystem.

### Domain contents

| Type | Responsibility |
|---|---|
| `Sitemaps/SitemapWriter` | Streaming, deterministic, dual-cap chunked XML + the index |
| `Sitemaps/SitemapChunk`, `SitemapUrl`, `SitemapIndexEntry`, `SitemapLimits`, `SitemapNames` | Values and protocol constants |
| `Urls/JobUrl`, `FacetUrl`, `SiteUrls`, `PrerenderKeys` | The only place a URL is shaped |
| `Urls/PagePath` | Validates a path that came from data (CMS pages, static config) before it becomes a URL |
| `Indexation/IndexationPolicy` | The 5 / 10 count gate |
| `Indexation/FacetCountProjector` | Folds one DB aggregate onto ~3,000 facets |
| `Indexation/FacetIndexabilityTracker` | Turns state into *transitions* |
| `Indexation/TurkishFold` | Turkish-aware folding, ported from the SPA's `foldSlug` |
| `Robots/RobotsPolicy` | `robots.txt` |
| `Ports/*` | `IJobCorpusReader`, `ICmsPageReader`, `ISitemapSink`, `IPrerenderCache`, `IFacetManifestSource` |

Time arrives as a `DateTimeOffset` parameter. `TimeProvider` is injected in the Worker and
never resolved statically, which is what makes the debounce and every emitted timestamp
assertable with a `FakeTimeProvider`.

---

## 2. Vertical slices

`Features/<Capability>/<UseCase>/`. Endpoints are discovered by assembly scan (`IEndpoint`).

| Slice | Role | Trigger | Does |
|---|---|---|---|
| `Sitemaps/RebuildAll` | builder | `PeriodicWorker`, 45 min | sync projection ← corpus, aggregate facets, read CMS pages, build all, stage+swap, log + publish in one commit |
| `Sitemaps/ApplyJobExpired` | reactor | `JobExpiredEvent` | commit removal → purge Garnet → signal flush |
| `Sitemaps/ApplyJobResurrected` | reactor | `JobResurrectedEvent` | commit re-admission → purge → signal |
| `Sitemaps/FlushDirty` | reactor | debounced signal | re-project `sitemap-jobs` (when dirty) + `sitemap-pages` (always), stage+swap |
| `Pages/ApplyCmsPagePublished` | reactor | `CmsPagePublishedEvent` | purge the path — and `PreviousPath` on a move → signal |
| `Pages/ApplyCmsPageUnpublished` | reactor | `CmsPageUnpublishedEvent` | purge the path → signal |
| `Indexing/SubmitIndexing` | reactor | called by both consumers | optional Google Indexing API, off by default |
| `Diagnostics/GetSitemapStatus` | any | `GET /api/seo/diag/sitemap` | last rebuild, per-file counts, dirty backlog |
| `Diagnostics/RebuildNow` | builder | `POST /api/seo/diag/rebuild` | manual full rebuild |

Role is a **registration decision**, not a folder axis (`Common/Roles/RolePlan`).

---

## 3. Roles

| Role | Runs | Replicas |
|---|---|---|
| `builder` | cron full rebuild + R2 write | **1** |
| `reactor` | freshness + CMS consumers, Garnet purge, debounced flush (also writes R2) | **1** |
| `all` | everything — the launch configuration | **1** |

`RolePlan.WritesToR2` is a named, tested property, and it is **true for both deployable
roles** — the incremental flush writes to the bucket too. That is why `reactor` is also
pinned to one replica, and why the startup warning fires on either.

`RolePlan.WritesToCompanyJob` is a property that is **always false**, asserted for every role
in `RolePlanTests` and again against the real table in `RebuildAllTests`. It is the single
most important thing this service does not do.

---

## 4. Losslessness — how the plan's guarantees are actually implemented

### 4.1 Inbound (PLAN §6.1)

Both consumers run behind `UseEntityFrameworkOutbox<SeoDbContext>` on a **single dedicated
queue** bound to both freshness exchanges. One queue rather than two, because expiry and
resurrection are opposite operations on the same `seo_url_state` row: on separate queues they
could interleave and leave a live job removed.

Ordering inside a consumer is deliberate and is the substance of the guarantee:

1. **Commit the state change** (row + inbox row, one transaction). Once this commits nothing
   can lose the removal.
2. **Then purge Garnet.** After, not inside — a purge is idempotent, so a crash in the gap
   costs a repeat rather than a correctness failure, whereas purging first would risk purging
   for a removal that then rolled back.
3. **Then raise the flush signal.**

### 4.2 Outbound (PLAN §6.2)

`AddEntityFrameworkOutbox<SeoDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })`,
registered for **every** role including one that consumes nothing — the builder publishes
`SitemapRebuiltEvent` in the same commit as the `seo_rebuild_log` row that records the swap.
Without it a broker outage in that gap loses the event *permanently*: the log row committed,
so the next rebuild's checksum comparison finds no difference and never re-emits.

### 4.3 R2 atomicity (PLAN §6.3)

The port has **no "upload this file" method**, and that is the design. Such a method makes a
torn set the default outcome. Instead a caller opens a stage, writes everything into it, and
commits — and `CommitAsync` promotes in a fixed order:

1. every changed child → live
2. the index → live (**last**)
3. obsolete files deleted (**only now**)
4. staging swept

At every instant the live index references only objects that exist. Promotion is a
server-side `CopyObject`, so the swap window does not grow with the corpus.

`FakeSitemapSink` in the integration tests enforces the same contract and its `FollowIndex`
helper **throws** if the live index ever names a missing file — the atomicity claim reduced
to an assertion.

### 4.4 Why R2 directly, and not through `kariyer-file-service`

This estate already has a service that owns R2, so the obvious question is why this one holds
its own credentials. It was asked, and the answer is that the two are solving different
problems.

`kariyer-file-service` owns **user files**: per-user, browser-uploaded, arbitrary names,
metadata worth tracking, a lifecycle worth auditing. Its API is shaped accordingly, and four
of those shapes are incompatible with publishing a sitemap:

| `kariyer-file-service` behaviour | Why it blocks this |
|---|---|
| `PresignedUploadHandler` builds the key: `{folder}/{category}/{ulid}-{name}` | **Fatal.** Cloudflare routes `/sitemap.xml` to a key of exactly `sitemap.xml`. A ULID-prefixed key is a file Google can never fetch. |
| Presign accepts `contentType` only | No `Content-Encoding: gzip`, so the `.gz` files are served as binary garbage; no `Cache-Control`. |
| No custom object metadata | The `seo-checksum` header disappears, so the conditional-write short-circuit dies and every cron tick re-uploads the whole set. |
| Every upload writes a `StoredFile` row, `Status="Pending"`, with an hourly orphan cleaner | ~6 rows per rebuild — ~70k rows/year of metadata describing files that overwrite the same six keys, next to a background job that deletes unconfirmed ones. |

And the decisive practical point: a presigned upload means the **bytes still travel from this
process straight to R2**. Routing through the file service would add an authentication
dependency and a second service to the sitemap-publish path without removing the S3 client
from this one.

The cost of the decision is contained by construction. Every line of R2-aware code lives in
`Common/Storage/SitemapSink`, behind the `ISitemapSink` port, and the integration suite runs
against `FakeSitemapSink` instead. If the file service ever grows an internal
publish-at-an-exact-key endpoint, moving to it is one implementation swap and no change to any
slice.

The credential this service holds should be scoped to the sitemap bucket (or prefix) alone —
see `DEPLOYMENT.md` §1. It has no reason to be able to read or delete a user's CV.

### 4.5 Reconstruction (PLAN §6.4)

`SyncUrlStatesFromCorpusAsync` re-derives `seo_url_state` from `company_job` at the start of
every rebuild: two set-based statements, one upserting every live job, one retiring anything
we still call live that the corpus no longer does.

That second statement is not a tidy-up. It is the **only** thing that catches a job leaving
the catalogue without a freshness event — an employer deactivating a posting in the panel, an
admin deleting one, the Node end-date cron. Freshness only publishes expiries *it* performed,
so those are the majority of departures, not the minority.

---

## 5. Facet counts — where this departs from the plan

PLAN §7 called for aggregates grouped per axis, and PLAN §8 named an `industry` column. Two
things were wrong with that and both are corrected here.

**`company_job` has no `industry` column.** The sector axis is carried by `department`, which
is what the jobs API filters on. Mapping a nonexistent column would have failed at the first
query; silently substituting one would have produced counts nobody could explain.

**Per-axis aggregates cannot answer a combo facet.** `count(city) × count(sector)` is not
`count(city AND sector)`, and two thirds of the manifest is combos. So the aggregate groups on
the whole tuple instead:

```sql
SELECT province, department, position, working_type, working_prefs, COUNT(*)
  FROM company_job
 WHERE status='approved' AND is_active AND NOT is_deleted AND slug_url <> ''
 GROUP BY 1,2,3,4,5
```

One query. The result is bounded by *distinct tuples* (thousands), not by the corpus
(hundreds of thousands), and every facet count is then a pure in-memory fold in
`FacetCountProjector` — bucketed by folded province so the fold is not |tuples| × |facets|.

Raw SQL because EF cannot translate a `GROUP BY` over a `text[]` column.

### The manifest carries axis VALUES, not just paths

This required a change in `kariyer-zamani-web`. A path alone cannot be counted:

```
/is-ilanlari/istanbul/yazilim  →  department IN ('Bilişim', 'Bilişim - İnternet')
```

The slug `yazilim` shares not one character with the values it selects. A service that folded
the slug and matched it against the column would count **zero** for exactly the highest-value
pages on the site, drop them from the sitemap, and disagree permanently with the SPA — which
would go on serving them as `index,follow`, because *its* count came through those same
`filterValues`.

So `scripts/generate-facet-manifest.ts` now emits `province`, `departments`, `positions`,
`workingTypes` and `workingPrefs` per entry, mapped through the same `toApiWorkingType` the
filter store uses. The curated registries stay the single source of truth, in the repo where
they already live. An entry that constrains nothing fails the generator, and is rejected again
defensively by `FacetManifestClient`.

### Matching rules

| Axis | Rule | Why |
|---|---|---|
| `province` | folded equality | the API filters location with an exact match |
| `department`, `position` | folded **substring**, OR'd | the API uses `iLike '%value%'` — which is why `Bilişim` legitimately selects `Bilişim - İnternet` |
| `working_type`, `working_prefs` | folded equality against any array **element**, OR'd | the API expands to `EXISTS (SELECT 1 FROM unnest(col) e WHERE e = 'Uzaktan')` |
| across axes | AND | as the API does |

Folding rather than raw comparison is the one deliberate divergence, and it is in the safe
direction: `tam zamanli` and `Tam Zamanlı` are the same preference to a human and to a URL.

---

## 6. Determinism and the conditional write

Three properties have to hold together for PLAN §7's short-circuit to be worth anything:

- **`SitemapWriter` output is byte-identical** for identical input — fixed encoding,
  indentation, `\n` newlines, invariant UTC-to-the-second `<lastmod>`, no ambient clock.
- **Reads are ordered.** `ORDER BY uid` on the corpus and on the projection is load-bearing:
  an unordered read would shuffle URLs between chunk files on every run and change every
  checksum.
- **The checksum is over the UNCOMPRESSED XML.** gzip output depends on the zlib the runtime
  links, so hashing compressed bytes would report a change on a base-image bump that moved not
  one URL — re-uploading the whole set on every deploy and destroying the signal's meaning.

A file is skipped only when **both** `seo_rebuild_log` *and* the live R2 object metadata carry
the computed checksum. The log alone would leave a file permanently missing from a bucket
restored from backup; R2 alone would re-upload everything whenever a metadata read failed.
Any disagreement falls through to "upload" — the safe direction.

### Identity includes the key

The third bullet above has a sharp edge that took a production change to find. Compression is
deliberately *not* part of a file's identity: the checksum is over the uncompressed XML, and
the live-checksum read strips `.gz` because `sitemap-jobs-1.xml` is the same document however
it was stored. Both are correct, and together they were wrong.

Flipping `Seo:R2:Compress` moves every stored key without moving a single byte of any
document. So the sink reported the live `sitemap-jobs-1.xml.gz` as an unchanged
`sitemap-jobs-1.xml`, the short-circuit skipped **every chunk**, and the index — whose own
bytes *did* change, because its children lost the suffix — was rewritten to name `.xml`
objects that had never been uploaded. An index of 404s, produced by the service itself, with
nothing failing and nothing to see.

The resolution is a distinction worth keeping straight: *content* identity excludes the
encoding, but *published* identity is the **key**, because the key is the URL a crawler
requests. `GetLiveChecksumsAsync` therefore accepts an object as live only when it sits where
the current configuration would write it, and logs the leftovers. See `DEPLOYMENT.md` §4 for
the cutover.

---

## 6a. Configuration collections bind by appending

Every collection-valued options property in this service initialises to `[]`, and that is a
rule rather than a style.

.NET's configuration binder **appends** bound values to a collection that is already
populated; it does not replace it. A property with entries in its initialiser therefore binds
to *defaults plus configuration*, never to configuration alone, and there is no binder setting
that changes it. `Seo:StaticPaths` and `Seo:DisallowedPaths` carried the same values in C# and
in `appsettings.json`, so both shipped doubled: `sitemap-static-1.xml.gz` served 14 `<url>`
entries for 7 pages — duplicate `<loc>` is invalid per the sitemaps protocol — and every
`Disallow` line in `robots.txt` appeared twice. Nothing detected it. It was found by reading
the published output of a production run.

So:

- **Values live in `appsettings.json`, which is the single source.** That costs no resilience:
  the file ships inside the image, so there is no deployment where the binary is present and
  the file is not.
- **Duplicates fail startup; they are not deduplicated.** A silent collapse would publish a
  correct sitemap while leaving configuration bound in a way its author does not believe,
  which hides the next occurrence. The rejection message names the *cause*, because nobody
  guesses "the binder appends" from a repeated URL.
- **An empty `StaticPaths` also fails startup**, since with the defaults gone an empty list
  means configuration did not supply one — and the rebuild would happily publish a valid,
  empty `<urlset>` and quietly stop advertising the home page. `DisallowedPaths` may be empty:
  `Allow: /` and nothing else is a coherent policy someone could mean.

`ConfigurationBindingTests` enforces this against the file that actually ships, including a
reflection sweep asserting that no bound options type anywhere in the service carries a
non-empty collection default. Tests over the C# defaults alone cannot see this class of bug —
they never put the defaults and the JSON in the same binder, which is the only arrangement in
which the two collide. That is why it shipped.

---

## 7. The debounce

`DirtySignal` is a single-slot `Channel` with `DropWrite`. The loop is: wait → sleep the
window → **drain** → flush once. A hundred expiries arriving together produce **one**
re-projection.

Draining happens *before* the flush, not after. A change committed while the projection is
streaming may have arrived after its row was read, so it is not in the file that went live —
its signal has to survive so the next iteration picks it up. Getting that backwards costs one
job sitting in the wrong state until the next full rebuild: invisible, and up to 45 minutes.

The signal is an **optimisation**. What durably records that a flush is owed is the `dirty`
column, which is why `FlushDirtyWorker` checks for unflushed rows at boot: a pod killed
between a consumer's commit and its flush recovers immediately instead of waiting a full cron
interval.

`IDirtyFlusher` exists so the coalescing can be tested with a `FakeTimeProvider` in
microseconds rather than against Postgres and a sink. What is being asserted there is a *cost*
property — a worker that flushed once per expiry still produces a correct sitemap, it just
re-streams a several-hundred-thousand-URL file a hundred times, which surfaces as an R2 bill
months later and in no test or metric before that.

---

## 8. Persistence

`SeoDbContext` owns the `seo` schema and nothing else:

| Table | Purpose |
|---|---|
| `seo_url_state` | incremental job-URL projection; a **cache**, never a ledger |
| `seo_facet_state` | last computed indexability — makes the facet event a *transition* |
| `seo_rebuild_log` | conditional-write short-circuit + the only audit trail that exists |
| `InboxState` / `OutboxState` / `OutboxMessage` | MassTransit |

`CompanyJobReadModel` is keyless and mapped with `ToView(null)`, which also keeps it out of
migrations — without that, EF would generate DDL to create a second, empty `company_job`
inside `seo`, and every corpus read would quietly return nothing.

`DatabaseMigrator` applies migrations at boot behind a Postgres advisory lock, on a key
distinct from the freshness service's: both migrate their own schema into the same database,
and sharing a key would make each one's rollout wait on the other's.

---

## 9. Observability

`DiagnosticsConfig` names the instruments the runbook refers to. The two worth paging on are
`seo_sitemap_rebuild_failures_total` and the **absence** of `seo_sitemap_rebuilds_total` — a
builder that has stopped still passes every health check.

Two more that read backwards from the usual intuition:

- `seo_sitemap_uploads_total` should be near **zero** between real changes. A steady stream
  means the checksum short-circuit is not working.
- `seo_dirty_flushes_total` should be far **fewer** than `seo_jobs_removed_total`. Equal means
  coalescing is not happening.

W3C propagators are explicit, so the trace that began in the freshness applier — where a job
was decided to be dead — continues into the removal and purge here. "Why is this URL still in
the sitemap" is one trace across two services.

---

## 10. CMS pages — the fourth URL source

`kariyer-cms-service` owns admin-authored landing pages in `cms.seo_page`. Their URLs reach
`sitemap.xml` through this service, in the same two layers used for jobs.

**Backstop — a read-only projection.** `CmsPageReadModel` maps `cms.seo_page`, keyless and
`ToView(null)`, exactly like `CompanyJobReadModel`. The rebuild emits `sitemap-pages.xml` from
it. Reading the table directly — rather than calling `GET /api/cms/pages/paths` — is what keeps
the prime directive intact: a rebuild never depends on the CMS being reachable, and
`<lastmod>` comes from `published_at` rather than from whatever an endpoint chooses to expose.
Sharing one Postgres is precisely what buys this, and is why the CMS was specified to share it.

**Latency — the events.** `CmsPagePublishedConsumer` and `CmsPageUnpublishedConsumer` bind
`seo.cms.consumer` to `cms.page.published` / `cms.page.unpublished`. They raise the debounce
signal so a publish appears within ~30s instead of up to a cron interval.

Three details worth knowing:

- **The consumers write no local state, and have no EF inbox.** Jobs need `seo_url_state`
  because the corpus is hundreds of thousands of rows. CMS pages number in the tens; the
  projection re-reads `cms.seo_page` on every flush, so there is nothing to cache that is
  cheaper than the truth — and nothing a redelivery could double-apply. A redelivery costs one
  repeated `DEL` and one repeated signal, both idempotent by nature.
- **The flush does NOT return early on `dirty == 0`.** A page publish raises the signal and
  leaves no dirty flag, so an early return would make every publish a no-op. The expensive
  half — streaming the live job corpus — is still guarded on `dirty > 0`; the page projection
  is cheap and always runs, and the checksum decides whether anything uploads.
- **A CMS-only flush still rebuilds the whole index.** `jobChunks` is empty in that case, and
  an index built from just-built chunks alone would omit `sitemap-jobs` entirely — the job
  catalogue vanishing the moment an editor publishes a landing page. The index is composed
  from what this flush built plus the rebuild log for everything it did not, and
  `TheFlushIndexStillNamesTheJobsFileWhenOnlyAPageChanged` pins it.

**The duplicated rule.** `SeoStore.CmsIndexablePredicate` restates
`Kariyer.Cms.Domain.Pages.PublicationPolicy.IsIndexable` in SQL. That duplication is a real
cost, accepted so a rebuild never needs an HTTP call — and it is only safe while something
asserts the two still agree. `CmsPageProjectionTests` is that something; if the CMS changes
what indexable means, those tests are what should fail.

Note the distinction the CMS draws and this service honours: `noindex` keeps a page out of the
sitemap without making it unreachable. A campaign or thank-you page is still a real page a
visitor can open.

---

## 11. What this service does not do

- **Write `company_job`.** Ever. Read-only projection, no write path, asserted twice.
- **Detect withdrawals.** The freshness service owns that.
- **Serve the sitemaps.** Cloudflare serves them from R2; there is no route here that returns
  one.
- **Emit JSON-LD or meta tags.** The SPA and the prerenderer own the page HTML.
- **Hardcode the facet list.** It comes from the manifest, validated but never invented.
