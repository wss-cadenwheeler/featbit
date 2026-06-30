\connect featbit

-- Control plane data center membership leases (Option A consistency).
-- One row per data center, keyed by dc_id. Mirrors the Mongo "dc_leases" collection.
CREATE TABLE IF NOT EXISTS dc_leases
(
    dc_id              text PRIMARY KEY,
    region             text,
    last_heartbeat_at  timestamptz,
    lease_expires_at   timestamptz,
    applied_watermarks jsonb
);

-- The live-set query filters on lease_expires_at > now().
CREATE INDEX IF NOT EXISTS ix_dc_leases_lease_expires_at
    ON dc_leases (lease_expires_at);
