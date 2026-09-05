-- Migration 001 — initial schema.
-- Forward-only. Runs inside one transaction managed by MigrationRunner.
-- The canonical `events` shape is fixed by BUILD_PLAN.md and does not change without an ADR.

------------------------------------------------------------------------------
-- Reference / configuration tables
------------------------------------------------------------------------------

CREATE TABLE roles (
    role_id      INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    description  TEXT NULL,
    is_system    INTEGER NOT NULL DEFAULT 1 CHECK (is_system IN (0, 1))
);

CREATE TABLE users (
    user_id               INTEGER PRIMARY KEY,
    username              TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name          TEXT NOT NULL,
    role_id               INTEGER NOT NULL REFERENCES roles(role_id),
    password_hash         TEXT NULL,
    must_change_password  INTEGER NOT NULL DEFAULT 0 CHECK (must_change_password IN (0, 1)),
    is_enabled            INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    failed_login_count    INTEGER NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
    locked_until_utc      TEXT NULL,
    last_login_utc        TEXT NULL,
    created_utc           TEXT NOT NULL
);

CREATE TABLE listeners (
    listener_id   INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    protocol      TEXT NOT NULL CHECK (protocol IN ('udp', 'tcp', 'tls', 'snmp', 'wineventlog')),
    bind_address  TEXT NOT NULL DEFAULT '0.0.0.0',
    port          INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    enabled       INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_utc   TEXT NOT NULL,
    UNIQUE (protocol, bind_address, port)
);

CREATE TABLE device_groups (
    group_id     INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    description  TEXT NULL,
    created_utc  TEXT NOT NULL
);

CREATE TABLE devices (
    device_id       INTEGER PRIMARY KEY,
    name            TEXT NOT NULL UNIQUE,
    primary_ip      TEXT NULL,
    hostname        TEXT NULL,
    vendor          TEXT NULL,
    notes           TEXT NULL,
    timezone        TEXT NULL,                 -- per-device tz override (VENDOR_SUPPORT.md)
    discovered      INTEGER NOT NULL DEFAULT 0 CHECK (discovered IN (0, 1)),
    first_seen_utc  TEXT NULL,
    last_seen_utc   TEXT NULL,
    created_utc     TEXT NOT NULL
);

CREATE TABLE device_group_members (
    group_id   INTEGER NOT NULL REFERENCES device_groups(group_id) ON DELETE CASCADE,
    device_id  INTEGER NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, device_id)
) WITHOUT ROWID;

CREATE TABLE streams (
    stream_id    INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    description  TEXT NULL,
    match_json   TEXT NULL,                    -- condition tree; semantics land in Phase 6
    is_system    INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1)),
    enabled      INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    sort_order   INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT NOT NULL
);

CREATE TABLE rules (
    rule_id          INTEGER PRIMARY KEY,
    name             TEXT NOT NULL UNIQUE,
    description      TEXT NULL,
    enabled          INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    priority         INTEGER NOT NULL DEFAULT 100,
    condition_json   TEXT NULL,
    actions_json     TEXT NULL,
    stop_processing  INTEGER NOT NULL DEFAULT 0 CHECK (stop_processing IN (0, 1)),
    created_utc      TEXT NOT NULL,
    updated_utc      TEXT NOT NULL
);

------------------------------------------------------------------------------
-- Event store (canonical schema — BUILD_PLAN.md)
------------------------------------------------------------------------------

CREATE TABLE events (
    -- AUTOINCREMENT: event_id is strictly increasing and never reused, so the
    -- search-index watermark (fts_state) is always safe.
    event_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    received_utc          TEXT NOT NULL,
    event_utc             TEXT NULL,
    source_ip             TEXT NOT NULL,
    hostname              TEXT NULL,
    app_name              TEXT NULL,
    proc_id               TEXT NULL,
    msg_id                TEXT NULL,
    facility              INTEGER NOT NULL CHECK (facility BETWEEN 0 AND 23),
    severity              INTEGER NOT NULL CHECK (severity BETWEEN 0 AND 7),
    protocol              TEXT NOT NULL CHECK (protocol IN ('udp', 'tcp', 'tls', 'snmp', 'wineventlog')),
    listener_id           INTEGER NULL REFERENCES listeners(listener_id) ON DELETE SET NULL,
    message               TEXT NOT NULL DEFAULT '',
    raw_message           BLOB NOT NULL,                 -- original bytes, verbatim (Constraint 4)
    search_text           TEXT NOT NULL DEFAULT '',      -- message, or a lossy UTF-8 view of raw_message when unparsed; FTS source only
    parse_status          TEXT NOT NULL CHECK (parse_status IN ('raw', 'rfc3164', 'rfc5424')),
    occurrence_count      INTEGER NOT NULL DEFAULT 1 CHECK (occurrence_count >= 1),
    structured_data_json  TEXT NULL,
    device_id             INTEGER NULL REFERENCES devices(device_id) ON DELETE SET NULL,
    vendor                TEXT NULL
);

CREATE TABLE event_fields (
    event_id  INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    name      TEXT NOT NULL,
    value     TEXT NOT NULL
);

CREATE TABLE event_streams (
    event_id   INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    stream_id  INTEGER NOT NULL REFERENCES streams(stream_id) ON DELETE CASCADE,
    PRIMARY KEY (event_id, stream_id)
) WITHOUT ROWID;

------------------------------------------------------------------------------
-- Full-text search (FTS5, external-content over events)
------------------------------------------------------------------------------
-- tokenchars keep IPs / MACs / paths as single tokens: "10.0.0.1", "aa:bb:cc:dd:ee:ff".
-- FTS is token/prefix based; arbitrary substring & regex search is Phase 5's fallback scan.
-- One indexed column (search_text) keeps the tokenisation cost down.
--
-- No sync triggers: an AFTER INSERT trigger firing per row is ~10x slower than the same
-- INSERT issued directly, so SqliteLogRepository owns FTS consistency explicitly — it
-- writes events_fts in the same transaction as the events rows, and deletes from it in
-- the retention path. 'rebuild' is available for repair (RebuildSearchIndexAsync).

CREATE VIRTUAL TABLE events_fts USING fts5(
    search_text,
    content = 'events',
    content_rowid = 'event_id',
    tokenize = "unicode61 remove_diacritics 2 tokenchars '.:-_/@'"
);

-- Search indexing is deferred off the ingest path (a per-row AFTER INSERT trigger is
-- ~10x too slow to meet the 20k rows/sec insert gate and the 5k msg/sec sustained
-- target). SearchIndexMaintainer copies new rows into events_fts in the background;
-- this row tracks how far it has got. See ADR 0009.
CREATE TABLE fts_state (
    id                     INTEGER PRIMARY KEY CHECK (id = 1),
    last_indexed_event_id  INTEGER NOT NULL DEFAULT 0
);
INSERT INTO fts_state (id, last_indexed_event_id) VALUES (1, 0);

------------------------------------------------------------------------------
-- Audit log — append-only in fact, not by convention (Phase 4 writes it)
------------------------------------------------------------------------------

CREATE TABLE audit_log (
    audit_id      INTEGER PRIMARY KEY,
    occurred_utc  TEXT NOT NULL,
    actor         TEXT NULL,
    action        TEXT NOT NULL,
    entity_type   TEXT NULL,
    entity_id     TEXT NULL,
    source_ip     TEXT NULL,
    before_json   TEXT NULL,
    after_json    TEXT NULL,
    detail        TEXT NULL
);

CREATE TRIGGER audit_log_no_update BEFORE UPDATE ON audit_log BEGIN
    SELECT RAISE(ABORT, 'audit_log is append-only');
END;

CREATE TRIGGER audit_log_no_delete BEFORE DELETE ON audit_log BEGIN
    SELECT RAISE(ABORT, 'audit_log is append-only');
END;

------------------------------------------------------------------------------
-- Indexes
------------------------------------------------------------------------------

CREATE INDEX ix_events_received_utc  ON events (received_utc);
CREATE INDEX ix_events_source_ip     ON events (source_ip);
CREATE INDEX ix_events_severity      ON events (severity);
CREATE INDEX ix_events_device_id     ON events (device_id);
CREATE INDEX ix_events_listener_id   ON events (listener_id);
CREATE INDEX ix_events_hostname_id   ON events (hostname, event_id);
CREATE INDEX ix_event_fields_name_value ON event_fields (name, value);
CREATE INDEX ix_event_fields_event_id   ON event_fields (event_id);
CREATE INDEX ix_event_streams_stream    ON event_streams (stream_id);
CREATE INDEX ix_audit_log_occurred_utc  ON audit_log (occurred_utc);
CREATE INDEX ix_devices_primary_ip      ON devices (primary_ip);
