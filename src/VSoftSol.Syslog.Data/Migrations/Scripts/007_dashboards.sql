-- Migration 007 — Phase 9: dashboards. Forward-only, one transaction.
-- A generic widget framework: every widget is a data source + an aggregation spec + a
-- visualization, stored as JSON on the dashboard row (there is no per-widget table — a
-- dashboard is edited and saved as a whole, the same shape as rules.actions_json). Plus a
-- lightweight collector-health sample table the "Collector Health" default dashboard reads
-- through the same aggregation path (ADR 0017).

------------------------------------------------------------------------------
-- Dashboards (PHASE_09 build item 4)
------------------------------------------------------------------------------
CREATE TABLE dashboards (
    dashboard_id         INTEGER PRIMARY KEY,
    owner_user_id        INTEGER NULL REFERENCES users(user_id) ON DELETE CASCADE,  -- NULL = shipped/system
    name                 TEXT NOT NULL,
    description          TEXT NULL,
    is_shared            INTEGER NOT NULL DEFAULT 0 CHECK (is_shared IN (0, 1)),
    is_system            INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1)),
    system_key           TEXT NULL UNIQUE,                       -- stable identity for a seeded dashboard (NULLs are distinct)
    default_range_seconds INTEGER NOT NULL DEFAULT 86400 CHECK (default_range_seconds > 0),
    refresh_seconds      INTEGER NOT NULL DEFAULT 0 CHECK (refresh_seconds >= 0),
    widgets_json         TEXT NOT NULL DEFAULT '[]',
    layout_json          TEXT NOT NULL DEFAULT '[]',
    created_utc          TEXT NOT NULL,
    updated_utc          TEXT NOT NULL,
    updated_by           TEXT NULL,
    -- a system dashboard has no owner; a user dashboard always does
    CHECK ((is_system = 1 AND owner_user_id IS NULL) OR (is_system = 0 AND owner_user_id IS NOT NULL))
);

CREATE INDEX ix_dashboards_owner ON dashboards (owner_user_id);
-- a user cannot have two dashboards with the same name; system names are globally unique
CREATE UNIQUE INDEX ux_dashboards_owner_name ON dashboards (owner_user_id, name) WHERE owner_user_id IS NOT NULL;
CREATE UNIQUE INDEX ux_dashboards_system_name ON dashboards (name) WHERE is_system = 1;

------------------------------------------------------------------------------
-- Collector-health samples (PHASE_09 build item 6 — the Collector Health dashboard).
-- Written by the collector-host sampler every N seconds; pruned to a short retention.
-- Read through the same aggregation / visualization path as event widgets. These are
-- process-wide operational counters, not per-tenant data — no scope filter applies.
------------------------------------------------------------------------------
CREATE TABLE collector_stat_samples (
    sample_id            INTEGER PRIMARY KEY,
    taken_utc            TEXT NOT NULL,
    committed_total      INTEGER NOT NULL DEFAULT 0,   -- cumulative; ingest rate is derived between samples
    channel_depth        INTEGER NOT NULL DEFAULT 0,
    channel_capacity     INTEGER NOT NULL DEFAULT 0,
    spill_frames         INTEGER NOT NULL DEFAULT 0,
    spill_bytes          INTEGER NOT NULL DEFAULT 0,
    database_bytes       INTEGER NOT NULL DEFAULT 0,
    disk_free_bytes      INTEGER NOT NULL DEFAULT 0,
    drops_total          INTEGER NOT NULL DEFAULT 0,   -- cumulative
    active_connections   INTEGER NOT NULL DEFAULT 0,
    quarantined_sources  INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_collector_stat_samples_taken ON collector_stat_samples (taken_utc);
