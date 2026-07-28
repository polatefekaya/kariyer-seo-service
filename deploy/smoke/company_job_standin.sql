-- A minimal stand-in for the Node application's company_job table.
--
-- Apply this ONLY against a throwaway database that does not already have it. On a test
-- server pointed at a copy of the app's schema, company_job already exists — skip this file.
--
-- Only the columns this service READS are here, and deliberately no more. If a query ever
-- starts depending on a column that is not listed, that dependency should be a conscious
-- decision rather than something that silently works because the real table happened to
-- have it.
--
-- Two things worth noting against the plan document:
--
--   * There is NO `industry` column. PLAN §8 named one, but company_job has no such column
--     — the sector axis is carried by `department`, which is what the jobs API filters on.
--     Mapping a column that does not exist would have failed at the first query; silently
--     substituting one would have produced facet counts nobody could explain.
--
--   * `working_type` and `working_prefs` are text[] ARRAYS, not scalars. That is why the
--     facet aggregate groups on them in SQL rather than unnesting in memory, and why the
--     matcher compares against array ELEMENTS.

CREATE TABLE IF NOT EXISTS public.company_job (
    uid             varchar(64) PRIMARY KEY,
    slug_url        varchar(500),
    status          text NOT NULL,
    is_active       boolean NOT NULL DEFAULT true,
    is_deleted      boolean NOT NULL DEFAULT false,
    modified_on     timestamptz,

    province        varchar(255),
    town            varchar(255),
    department      varchar(255),
    position        varchar(255),
    working_type    text[] NOT NULL DEFAULT '{}',
    working_prefs   text[] NOT NULL DEFAULT '{}'
);

-- The corpus predicate is `status='approved' AND is_active AND NOT is_deleted`, and every
-- query this service makes is filtered by it. Without this index each rebuild is a
-- sequential scan of the whole catalogue — twice, once to stream and once to aggregate.
CREATE INDEX IF NOT EXISTS ix_company_job_live
    ON public.company_job (status, is_active, is_deleted);

-- The facet aggregate groups on these five columns. The index will not usually be USED for
-- a full-table group-by, but it is what keeps the plan from degrading once the live subset
-- becomes a small fraction of a large table.
CREATE INDEX IF NOT EXISTS ix_company_job_facets
    ON public.company_job (province, department, position)
    WHERE status = 'approved' AND is_active = true AND is_deleted = false;
