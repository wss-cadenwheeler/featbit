\connect featbit

-- S1: committed-vs-pending for segments (Postgres parity with the Mongo path).
-- committed_version: monotonic version of the last COMMITTED change (default 0).
-- pending: a staged-but-not-committed change, stored as jsonb (NULL when none).
ALTER TABLE segments
    ADD COLUMN committed_version bigint NOT NULL DEFAULT 0,
    ADD COLUMN pending jsonb NULL;
