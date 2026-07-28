-- A minimal stand-in for kariyer-cms-service's `cms.seo_page`.
--
-- Apply this ONLY against a throwaway database where the CMS service has not migrated. In any
-- environment where it has, the real table exists — skip this file.
--
-- Only the columns THIS service reads, and deliberately no more. `layout`, `slug`,
-- `default_locale`, `locales`, `created_by`, `updated_by` and the rest are the CMS's business;
-- if a query here ever starts depending on one of them, that should be a conscious decision
-- rather than something that silently works because the real table happened to have it.
--
-- Kept in sync with kariyer-cms-service/docs/schema.sql by CmsPageProjectionTests, which fails
-- if the indexable predicate stops agreeing with the CMS's own PublicationPolicy.

CREATE SCHEMA IF NOT EXISTS cms;

CREATE TABLE IF NOT EXISTS cms.seo_page (
    id               uuid PRIMARY KEY,
    path             varchar(512) NOT NULL UNIQUE,
    status           varchar(16)  NOT NULL,
    published_layout jsonb,
    noindex          boolean      NOT NULL DEFAULT false,
    published_at     timestamptz
);

-- The projection's only query: published pages, in path order. Partial, because the published
-- set is the minority on a CMS where drafts accumulate.
CREATE INDEX IF NOT EXISTS ix_seo_page_indexable
    ON cms.seo_page (path)
    WHERE status = 'published' AND published_layout IS NOT NULL AND noindex = false;
