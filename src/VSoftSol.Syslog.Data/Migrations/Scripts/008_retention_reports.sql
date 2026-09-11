-- Migration 008 — Phase 10: retention tiering, tamper-evident archives, restores, and the
-- report engine. Forward-only, one transaction (MigrationRunner).

------------------------------------------------------------------------------
-- Retention policy — global defaults + per-stream overrides (PHASE_10 build item 1)
------------------------------------------------------------------------------
CREATE TABLE retention_settings (
    id                  INTEGER PRIMARY KEY CHECK (id = 1),
    default_hot_days    INTEGER NOT NULL DEFAULT 30  CHECK (default_hot_days >= 0),
    default_warm_days   INTEGER NOT NULL DEFAULT 90  CHECK (default_warm_days >= 0),
    default_cold_days   INTEGER NOT NULL DEFAULT 365 CHECK (default_cold_days >= 0),
    archive_root        TEXT NOT NULL DEFAULT '',
    compression_level   INTEGER NOT NULL DEFAULT 3 CHECK (compression_level BETWEEN 1 AND 19),
    batch_size          INTEGER NOT NULL DEFAULT 500 CHECK (batch_size > 0),
    updated_utc         TEXT NULL,
    updated_by          TEXT NULL
);
INSERT INTO retention_settings (id) VALUES (1);

-- A stream's own retention row. Where an event belongs to more than one stream, its
-- *primary* stream (the most specific — lowest stream_id among its non-catch-all
-- memberships, falling back to the catch-all) owns the policy. See ADR 0018.
CREATE TABLE retention_policies (
    stream_id           INTEGER PRIMARY KEY REFERENCES streams(stream_id) ON DELETE CASCADE,
    hot_days            INTEGER NOT NULL CHECK (hot_days >= 0),
    warm_days           INTEGER NOT NULL CHECK (warm_days >= 0),
    cold_days           INTEGER NOT NULL CHECK (cold_days >= 0),
    archive_path        TEXT NULL,
    compression_level   INTEGER NOT NULL DEFAULT 3 CHECK (compression_level BETWEEN 1 AND 19),
    updated_utc         TEXT NULL,
    updated_by          TEXT NULL
);

------------------------------------------------------------------------------
-- Tier bookkeeping on live events (PHASE_10 build item 2/3)
------------------------------------------------------------------------------
ALTER TABLE events ADD COLUMN tier TEXT NOT NULL DEFAULT 'hot' CHECK (tier IN ('hot', 'warm'));
CREATE INDEX ix_events_tier_id ON events (tier, event_id) WHERE tier = 'warm';

-- One checkpoint per tiering phase — global, not per-stream (the primary-stream rule
-- above means one global age-ordered sweep is correct and simpler than N per-stream
-- sweeps). Resumable after a restart: a batch only advances the checkpoint after it
-- commits, so a kill mid-batch just repeats that batch next tick (PHASE_10 build item 2).
CREATE TABLE tiering_checkpoints (
    phase           TEXT PRIMARY KEY CHECK (phase IN ('warm', 'cold', 'restore_expiry', 'verify')),
    last_event_id   INTEGER NOT NULL DEFAULT 0,
    updated_utc     TEXT NOT NULL
) WITHOUT ROWID;
INSERT INTO tiering_checkpoints (phase, last_event_id, updated_utc) VALUES
    ('warm', 0, '1970-01-01T00:00:00.0000000Z'),
    ('cold', 0, '1970-01-01T00:00:00.0000000Z'),
    ('restore_expiry', 0, '1970-01-01T00:00:00.0000000Z'),
    ('verify', 0, '1970-01-01T00:00:00.0000000Z');

------------------------------------------------------------------------------
-- Archives — the tamper-evidence record (PHASE_10 build item 4)
------------------------------------------------------------------------------
CREATE TABLE archives (
    archive_id        INTEGER PRIMARY KEY,
    stream_id         INTEGER NULL REFERENCES streams(stream_id) ON DELETE SET NULL,
    stream_name       TEXT NOT NULL,          -- denormalised; survives stream deletion
    file_path         TEXT NOT NULL UNIQUE,
    period_start_utc  TEXT NOT NULL,
    period_end_utc    TEXT NOT NULL,
    event_count       INTEGER NOT NULL CHECK (event_count >= 0),
    byte_size         INTEGER NOT NULL CHECK (byte_size >= 0),
    sha256            TEXT NOT NULL,
    status            TEXT NOT NULL DEFAULT 'ok' CHECK (status IN ('ok', 'tamper_detected', 'missing', 'deleted')),
    created_utc       TEXT NOT NULL,
    verified_utc      TEXT NULL,
    deleted_utc       TEXT NULL
);
CREATE INDEX ix_archives_stream_period ON archives (stream_id, period_start_utc);
CREATE INDEX ix_archives_status ON archives (status) WHERE status NOT IN ('ok', 'deleted');
CREATE INDEX ix_archives_created ON archives (created_utc);

------------------------------------------------------------------------------
-- Restores — a temporary, auditable, auto-expiring reinstatement (PHASE_10 build item 5)
------------------------------------------------------------------------------
CREATE TABLE archive_restores (
    restore_id      INTEGER PRIMARY KEY,
    archive_id      INTEGER NOT NULL REFERENCES archives(archive_id) ON DELETE CASCADE,
    requested_by    TEXT NOT NULL,
    requested_utc   TEXT NOT NULL,
    expires_utc     TEXT NOT NULL,
    event_count     INTEGER NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'expired')),
    expired_utc     TEXT NULL
);
CREATE INDEX ix_archive_restores_active ON archive_restores (status, expires_utc) WHERE status = 'active';

-- A restored event is reinserted with its ORIGINAL event_id (AUTOINCREMENT never reuses
-- ids, so this cannot collide) and tagged with the restore that brought it back, so
-- expiry can find and remove exactly those rows again.
ALTER TABLE events ADD COLUMN restore_id INTEGER NULL REFERENCES archive_restores(restore_id) ON DELETE CASCADE;
CREATE INDEX ix_events_restore ON events (restore_id) WHERE restore_id IS NOT NULL;

------------------------------------------------------------------------------
-- Reports — templates, schedules, runs (PHASE_10 build items 6/8)
------------------------------------------------------------------------------
CREATE TABLE reports (
    report_id           INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    template_key        TEXT NOT NULL,        -- a CannedReportCatalog key, or 'custom'
    saved_search_id     INTEGER NULL REFERENCES saved_searches(saved_search_id) ON DELETE SET NULL,
    query_text          TEXT NULL,             -- inline query for a custom report
    time_range_days     INTEGER NOT NULL DEFAULT 90 CHECK (time_range_days > 0),
    schedule            TEXT NOT NULL DEFAULT 'none' CHECK (schedule IN ('none', 'daily', 'weekly', 'monthly')),
    delivery_json       TEXT NOT NULL DEFAULT '{}',
    owner_user_id       INTEGER NULL REFERENCES users(user_id) ON DELETE SET NULL,
    is_system           INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1)),
    enabled             INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_utc         TEXT NOT NULL,
    updated_utc         TEXT NULL,
    updated_by          TEXT NULL,
    last_run_utc        TEXT NULL,
    next_run_utc        TEXT NULL
);
CREATE INDEX ix_reports_owner ON reports (owner_user_id);
CREATE INDEX ix_reports_due ON reports (next_run_utc) WHERE schedule <> 'none' AND enabled = 1;
-- A data-integrity guard, not an upsert conflict target (Phase 9 ADR 0017 lesson: a
-- partial unique index cannot serve as an ON CONFLICT target). Seeding checks-then-writes.
CREATE UNIQUE INDEX ux_reports_system_template ON reports (template_key) WHERE is_system = 1;

CREATE TABLE report_runs (
    run_id           INTEGER PRIMARY KEY,
    report_id        INTEGER NOT NULL REFERENCES reports(report_id) ON DELETE CASCADE,
    started_utc      TEXT NOT NULL,
    completed_utc    TEXT NULL,
    status           TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'ok', 'failed')),
    row_count        INTEGER NULL,
    pdf_path         TEXT NULL,
    csv_path         TEXT NULL,
    error            TEXT NULL,
    delivered_utc    TEXT NULL,
    delivery_error   TEXT NULL,
    triggered_by     TEXT NOT NULL             -- username, or 'scheduler'
);
CREATE INDEX ix_report_runs_report ON report_runs (report_id, started_utc DESC);

-- One global SMTP profile for scheduled report delivery (mirrors discovery_settings).
CREATE TABLE report_smtp_settings (
    id             INTEGER PRIMARY KEY CHECK (id = 1),
    host           TEXT NULL,
    port           INTEGER NOT NULL DEFAULT 587 CHECK (port BETWEEN 1 AND 65535),
    from_address   TEXT NULL,
    username       TEXT NULL,
    secret_name    TEXT NULL,
    use_tls        INTEGER NOT NULL DEFAULT 1 CHECK (use_tls IN (0, 1)),
    updated_utc    TEXT NULL,
    updated_by     TEXT NULL
);
INSERT INTO report_smtp_settings (id) VALUES (1);
