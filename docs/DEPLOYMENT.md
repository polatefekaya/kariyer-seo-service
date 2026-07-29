# kariyer-seo-service — DEPLOYMENT

> How to run it, and the four things outside this repository that have to be true for it to
> matter at all.

---

## 0. Launch shape

**`SERVICE_ROLE=all`, one replica.** That is the whole deployment (PLAN §5).

The replica count is not a capacity decision, it is a correctness one. Two replicas staging
`sitemap.xml` concurrently can swap in each other's half-finished index, and the only party
who ever finds out is the crawler that fetched during the window — there is no error, no log
line, and no metric for it. The service prints a warning at startup naming itself as the
holder of the R2 write; exactly one pod should ever print it.

If the cron rebuild later needs to be separated from the event-reactive path, split into
`builder` + `reactor` — **each still at one replica**, because the incremental flush writes to
the bucket too.

---

## 1. Prerequisites

| Dependency | Purpose | Failure behaviour |
|---|---|---|
| Postgres (the jobs database) | `seo` schema + read-only `company_job` | readiness fails; pod pulled from rotation |
| RabbitMQ | freshness events in, SEO events out | readiness fails; outbox holds events |
| Cloudflare R2 bucket | the published sitemaps | rebuild fails, live set unchanged, **health stays green** |
| Garnet | prerender cache purges | purge retried; cache TTL is the backstop |
| `kariyer-zamani-web` | `/seo/facet-manifest.json` | last good manifest reused; filter sitemap goes stale, never empty |
| `kariyer-cms-service` | `cms.seo_page` (read-only) + its two events | rebuild fails if the schema is absent; without the events, CMS pages reach the sitemap on the cron instead of in ~30s |
| GitHub Packages | `Kariyer.Messaging.Contracts` **≥ 1.2.0** | build/restore fails |

> **The R2 row is the dangerous one.** A missing bucket does not fail readiness — this service
> serves no traffic, so failing readiness on it would take the pod out of rotation for a
> dependency that matters every 45 minutes. It fails the *rebuild*, into a log line, while
> `/health` and `/health/ready` both stay green and the live sitemap silently ages. That is
> why the incomplete-bucket check is a **startup** validation instead.

### The R2 credential — scope it narrowly

This service holds its own R2 token rather than going through `kariyer-file-service`. The
reasoning is in `ARCHITECTURE.md` §4.4; the short version is that the file service generates
its own ULID-prefixed keys, and a sitemap has to live at exactly `sitemap.xml` or Cloudflare
cannot route to it.

The consequence is a second R2 credential in the estate, so **scope it to the sitemap bucket
— or to `Seo:R2:Prefix` within a shared one — and to object read/write/delete only.** This
process has no reason to be able to read or delete a user's CV, and a token that can is the
one genuine downside of this decision. Everything else about it is contained: all R2-aware
code is a single class behind a port.

Note also that the service only ever deletes keys that pass `SitemapNames.IsOwned` — a bug in
the obsolete-set calculation cannot empty a bucket it shares.

---

## 2. Contracts package

The service references `Kariyer.Messaging.Contracts` **1.2.0**: `Seo/` (added in 1.1.0) for the
events it publishes, and `Cms/` (added in 1.2.0) for the two it consumes from
`kariyer-cms-service`. Publish it before the first build:

```bash
cd ../kariyer-messaging-contracts
./prepare.sh && ./publish.sh     # needs GITHUB_USER + GITHUB_TOKEN in .env
```

Restore fails below 1.2.0 — `CmsPagePublishedEvent` does not exist there.

---

## 3. Configuration

Everything binds from `appsettings.json` and is overridable by environment variable using the
`Section__Key` convention. Secrets should arrive as environment variables or mounted files,
never in the image.

### Required

```bash
SERVICE_ROLE=all

ConnectionStrings__Postgres='Host=…;Database=kariyer;Username=…;Password=…'
ConnectionStrings__RabbitMQ='amqp://user:pass@rabbit:5672/'

Seo__SiteUrl='https://kariyerzamani.com'

Seo__R2__Endpoint='https://<account>.r2.cloudflarestorage.com'
Seo__R2__Bucket='kariyer-seo'
Seo__R2__AccessKey='…'
Seo__R2__SecretKey='…'

Garnet__ConnectionString='garnet:6379'
```

The CMS integration needs no configuration: `Persistence:CmsSchema` defaults to `cms` in the
same database, and the exchange and queue names (`cms.page.published`,
`cms.page.unpublished`, `seo.cms.consumer`) default to what `kariyer-cms-service` publishes.
If the CMS has not been deployed yet, the `cms` schema will not exist and the rebuild will
fail on the page projection — deploy it first, or point `Persistence__CmsSchema` at a schema
that has a `seo_page` table.

`Seo__SiteUrl` must match the SPA's canonical host **exactly** — https, no `www.` the site
does not use, no trailing path. A mismatch does not throw; it publishes several hundred
thousand URLs that all redirect, which burns crawl budget and tells Google the file is stale.
Startup rejects the obvious forms of this.

### Worth knowing

| Key | Default | Note |
|---|---|---|
| `Seo:CronInterval` | `00:45:00` | This **is** the staleness bound the service promises |
| `Seo:DebounceWindow` | `00:00:30` | Bounds how long a withdrawn job stays advertised |
| `Seo:Thresholds:SingleAxis` / `:Combo` | `5` / `10` | Mirror `src/seo/facets/indexation.ts` — **change together** |
| `Seo:R2:Prefix` | `""` | Key prefix inside the bucket |
| `Seo:R2:StagingPrefix` | `_staging/` | Must **not** match the Cloudflare `/sitemap*.xml` rule |
| `Seo:R2:Compress` | `true` | Stores `.xml.gz` with `Content-Encoding: gzip` |
| `Seo:CacheControl` | `public, max-age=600` | Shorter than the cron interval, deliberately |
| `Persistence:MigrateOnStartup` | `true` | See below |
| `Indexing:Enabled` | `false` | Optional; see §7 |

### Database permissions

With `MigrateOnStartup=true` (the default) the service needs DDL rights on the `seo` schema.
To avoid that, apply `docs/schema.sql` as a one-shot job and set it to `false` — the running
service then needs only DML on `seo` and **SELECT on `public.company_job`**.

It never needs INSERT, UPDATE or DELETE on `company_job`. Granting them would not break
anything today; withholding them makes PLAN §1 enforceable by the database rather than by
code review.

### Non-production hosts (tst, staging, preview)

**Set `Seo__AllowIndexing=false` on every host that is not production.** With it false the
service still builds and publishes the whole sitemap set — so the pipeline is fully
verifiable — but `robots.txt` becomes:

```
User-agent: *
Disallow: /
```

with **no `Sitemap:` line**, because advertising a sitemap you have just told crawlers to
ignore is a contradiction and some crawlers follow it anyway.

The reason this matters more than it looks: a test deployment is a *complete copy* of the site
at a different hostname. Left crawlable it competes with production for the same queries on the
same content, and Google resolves that by picking a winner itself. Handing it a sitemap of
every job URL makes that outcome far more likely, because a sitemap is the most effective
discovery mechanism there is.

Also set the two host-dependent values, or the test deployment will publish URLs pointing at
**production**:

```bash
Seo__AllowIndexing=false
Seo__SiteUrl='https://tst.kariyerzamani.com'
Seo__FacetManifest__Url='https://tst.kariyerzamani.com/seo/facet-manifest.json'
```

`Seo__SiteUrl` also drives the Garnet prerender keys, so it must match whatever the
prerenderer on that host renders under, or every purge silently removes nothing.

> **`Disallow: /` is not enough to REMOVE a host that is already indexed.** It blocks
> crawling, not indexing — and because the crawler may no longer fetch the page, it can never
> see a `noindex` that would remove it. For a host that has already been crawled, serve
> `X-Robots-Tag: noindex` (which requires crawling to stay allowed) or put the host behind
> authentication. Check with `site:tst.kariyerzamani.com` before assuming this file is
> sufficient.

---

## 4. Cloudflare — serving the files

The service writes to R2 and **does not serve anything** (PLAN §1). Cloudflare does.

Bind the bucket and route these paths to it:

```
/sitemap.xml            → R2  <bucket>/<prefix>sitemap.xml.gz
/sitemap-*.xml          → R2  <bucket>/<prefix>sitemap-*.xml.gz
/robots.txt             → R2  <bucket>/<prefix>robots.txt
```

Three things to get right:

1. **The `.gz` suffix.** With `Seo:R2:Compress=true` the stored keys end in `.xml.gz` and
   carry `Content-Encoding: gzip`. The sitemap index generated by this service already names
   the children *with* the suffix, so the URLs a crawler follows are
   `https://kariyerzamani.com/sitemap-jobs-1.xml.gz`. Whatever rule you write, the URL in the
   index and the object it resolves to must agree — an index of 404s is the failure mode, and
   nothing here would report it.
2. **`_staging/` must not be reachable.** The rule above cannot match it, and that is
   load-bearing: a staging key served to Googlebot is a half-written file, which defeats the
   entire point of the stage-and-swap.
3. **Do not let the edge cache outlive the rebuild.** Objects carry
   `Cache-Control: public, max-age=600` from `Seo:CacheControl`; keep it below
   `Seo:CronInterval`.

Verify after the first rebuild:

```bash
curl -sI https://kariyerzamani.com/robots.txt
curl -s  https://kariyerzamani.com/sitemap.xml.gz | gunzip | head -20
```

---

## 5. Search Console — submit once

Submit `https://kariyerzamani.com/sitemap.xml` **once**, and never again.

Google retired the sitemap ping endpoint; there is nothing to call on each rebuild.
Re-submitting does not accelerate anything. `<lastmod>` on each index entry is the freshness
signal, which is why this service propagates each child's newest `<lastmod>` into the index
rather than stamping the rebuild time — a file that claims everything changed every 45 minutes
teaches the crawler to ignore the field.

`robots.txt` carries the `Sitemap:` line for every other crawler, and it is rebuilt with the
set so the reference and the index can never drift apart.

Optional: an IndexNow ping on expiry/resurrection for Bing. Not implemented.

---

## 6. First run

1. Publish contracts 1.2.0 (§2), and deploy kariyer-cms-service so the `cms` schema exists.
2. Deploy at **1 replica** with `SERVICE_ROLE=all`.
3. Confirm the boot log shows the role and the R2-write warning **once**.
4. Force the first rebuild instead of waiting 45 minutes:
   ```bash
   kubectl exec deploy/kariyer-seo -- curl -sX POST localhost:8080/api/seo/diag/rebuild
   ```
5. Check what it produced:
   ```bash
   kubectl exec deploy/kariyer-seo -- curl -s localhost:8080/api/seo/diag/sitemap | jq
   ```
6. Add the Cloudflare rules (§4) and verify with `curl` (§4).
7. Submit the index to Search Console (§5).

The `dirtyUrls` field on the diagnostics endpoint is the one to watch afterwards: non-zero and
not falling means the debounced flush is not running.

---

## 7. Google Indexing API (optional)

Off by default, honestly so: quota-limited to 200 URLs/day, supports `JobPosting` only, and
needs a service account added as an **owner** of the Search Console property.

```bash
Indexing__Enabled=true
Indexing__CredentialsPath=/secrets/google-indexing.json     # mounted file, not an env var
Indexing__DailyQuota=200
```

`Enabled` means **usable**: `true` without a readable key file fails startup. That is
deliberate — the submitter never throws, so a missing key at runtime is indistinguishable from
the feature being switched off.

---

## 8. Failure modes

| Failure | Behaviour |
|---|---|
| Broker down while publishing | Outbox holds the event; delivered on reconnect. No loss. |
| Process dies mid-rebuild | Staging keys never promoted; live set unchanged. Next tick redoes it. |
| R2 unreachable | Rebuild fails, live set unchanged, **health stays green** — alert on `seo_sitemap_rebuild_failures_total` and on the absence of `seo_sitemap_rebuilds_total`. |
| Garnet down on purge | Logged and counted, never fatal. Retried on redelivery; prerender TTL is the backstop. |
| Web app down at manifest-fetch time | Last good manifest reused (6 h cache). If nothing is cached the filter sitemap is not rebuilt and the previous file stays live. **Never** an empty file. |
| Duplicate freshness event | Inbox dedup → no-op. |
| `company_job` schema drift | Integration tests fail in CI against `deploy/smoke/company_job_standin.sql`. |
| Two replicas by accident | The one thing with no automatic protection. Both print the R2-write warning; alert on more than one. |

---

## 9. Runbook

**"A withdrawn job is still in the sitemap."**
`GET /api/seo/diag/sitemap` → is `dirtyUrls` non-zero and static? The flush is stuck. Check
`seo_worker_tick_failures_total{worker="flush-dirty"}`. Worst case the next cron corrects it.

**"A live job is not in the sitemap."**
Check the corpus predicate first: `status='approved' AND is_active AND NOT is_deleted` and a
non-empty `slug_url`. A job failing any of those is correctly absent.

**"A facet page is `index,follow` but not in the sitemap."**
`seo_facet_state.job_count` versus the threshold for its `axes`. If the counts disagree with
what the page shows, the manifest's axis values and the API's filter have diverged — regenerate
`facet-manifest.json` in the web repo.

**"The sitemap stopped changing."**
The most likely cause is a rebuild failing every tick. The loop survives failures by design,
so the pod looks perfectly healthy. `seo_sitemap_rebuild_failures_total` is the metric;
`lastRebuiltAt` on the diagnostics endpoint is the confirmation.
