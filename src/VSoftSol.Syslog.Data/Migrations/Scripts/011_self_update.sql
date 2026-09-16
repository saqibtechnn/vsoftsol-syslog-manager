-- Migration 011 — v1.1: self-update check settings and last-known-release state (ADR 0021).
-- Forward-only, one transaction (MigrationRunner).

------------------------------------------------------------------------------
-- One global row (mirrors report_smtp_settings / discovery_settings). Off by default —
-- no outbound check happens until an Administrator explicitly enables it.
------------------------------------------------------------------------------
CREATE TABLE update_settings (
    id                    INTEGER PRIMARY KEY CHECK (id = 1),
    check_enabled         INTEGER NOT NULL DEFAULT 0 CHECK (check_enabled IN (0, 1)),
    check_interval_hours  INTEGER NOT NULL DEFAULT 24 CHECK (check_interval_hours BETWEEN 1 AND 168),
    last_checked_utc      TEXT NULL,
    last_check_error      TEXT NULL,
    latest_known_version  TEXT NULL,
    latest_manifest_json  TEXT NULL,
    downloaded_msi_path   TEXT NULL,
    downloaded_msi_sha256 TEXT NULL,
    updated_utc           TEXT NULL,
    updated_by            TEXT NULL
);
INSERT INTO update_settings (id, check_enabled, check_interval_hours) VALUES (1, 0, 24);
