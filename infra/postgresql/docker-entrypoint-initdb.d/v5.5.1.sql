\connect featbit

-- B4: committed-vs-pending for feature_flags (Postgres parity with the Mongo path).
-- committed_version: monotonic version of the last COMMITTED change (default 0).
-- pending: a staged-but-not-committed change, stored as jsonb (NULL when none).
ALTER TABLE feature_flags
    ADD COLUMN committed_version bigint NOT NULL DEFAULT 0,
    ADD COLUMN pending jsonb NULL;
