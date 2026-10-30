-- Runs once, when the Postgres volume is first created.

CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

CREATE TABLE links (
    -- Insertion order; used as the keyset cursor for "list my links".
    id          bigint      GENERATED ALWAYS AS IDENTITY,
    -- Random 7-char base62 code, or a custom alias (3-32 chars).
    code        varchar(32) PRIMARY KEY,
    long_url    text        NOT NULL,
    -- API key name that created the link; NULL for anonymous links (which nobody can list or delete).
    owner       text        NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NULL,
    deleted_at  timestamptz NULL,
    -- Maintained by the analytics worker in batches, never by the redirect path.
    click_count bigint      NOT NULL DEFAULT 0
);

CREATE INDEX links_owner_id_idx ON links (owner, id DESC)
    WHERE owner IS NOT NULL AND deleted_at IS NULL;

-- Per-day click counts for the stats endpoint. No foreign key on purpose:
-- one bad code must not fail a whole batch insert in the worker.
CREATE TABLE link_clicks_daily (
    code   varchar(32) NOT NULL,
    day    date        NOT NULL,
    clicks bigint      NOT NULL,
    PRIMARY KEY (code, day)
);
