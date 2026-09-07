-- Migration 003 — Phase 5 search: saved searches and per-user grid column layouts.
-- Forward-only. Runs inside one transaction managed by MigrationRunner.

------------------------------------------------------------------------------
-- Saved searches (PHASE_05 build item 6)
------------------------------------------------------------------------------
-- Stores the query, never the results. Owned by a user; optionally shared read-only with
-- everyone. A one-click promote to a dashboard widget (Phase 9) or alert (Phase 8) reads
-- query_text from here.

CREATE TABLE saved_searches (
    saved_search_id  INTEGER PRIMARY KEY,
    owner_user_id    INTEGER NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    name             TEXT NOT NULL,
    query_text       TEXT NOT NULL DEFAULT '',
    time_range_json  TEXT NULL,                 -- the saved relative/absolute range, or NULL for "current"
    is_shared        INTEGER NOT NULL DEFAULT 0 CHECK (is_shared IN (0, 1)),
    created_utc      TEXT NOT NULL,
    updated_utc      TEXT NOT NULL,
    UNIQUE (owner_user_id, name)
);

CREATE INDEX ix_saved_searches_owner  ON saved_searches (owner_user_id);
CREATE INDEX ix_saved_searches_shared ON saved_searches (is_shared) WHERE is_shared = 1;

------------------------------------------------------------------------------
-- Per-user results-grid column layouts (PHASE_05 build item 3)
------------------------------------------------------------------------------
-- Which columns are shown, their order and widths. layout_json is opaque to the DB;
-- the grid component owns its shape. One layout per user may be the default.

CREATE TABLE user_column_layouts (
    layout_id     INTEGER PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    layout_json   TEXT NOT NULL,
    is_default    INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
    created_utc   TEXT NOT NULL,
    updated_utc   TEXT NOT NULL,
    UNIQUE (user_id, name)
);

CREATE INDEX ix_user_column_layouts_user ON user_column_layouts (user_id);

------------------------------------------------------------------------------
-- User-authored extractors (PHASE_05 item 8 — the pattern tester "save as extractor")
------------------------------------------------------------------------------
-- A GROK or regex pattern the operator built and verified against a sample in the pattern
-- tester. Stored here now; wiring these into the ingest extractor pipeline (priority,
-- vendor scoping, hot reload) is part of the Phase 6 configuration surface.

CREATE TABLE user_extractors (
    extractor_id  INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    kind          TEXT NOT NULL CHECK (kind IN ('grok', 'regex')),
    pattern       TEXT NOT NULL,
    sample        TEXT NULL,
    enabled       INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_by    TEXT NULL,
    created_utc   TEXT NOT NULL,
    updated_utc   TEXT NOT NULL
);
