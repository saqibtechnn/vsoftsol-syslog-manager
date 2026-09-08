-- Migration 004 — Phase 6: device registry (extended), auto-discovery, multi-IP mapping,
-- and structured stream match rules. Forward-only, one transaction.

------------------------------------------------------------------------------
-- Device registry — the Phase 6 fields (PHASE_06 build item 1)
------------------------------------------------------------------------------
ALTER TABLE devices ADD COLUMN model              TEXT NULL;
ALTER TABLE devices ADD COLUMN role               TEXT NULL;      -- switch / router / firewall / server / …
ALTER TABLE devices ADD COLUMN site               TEXT NULL;
ALTER TABLE devices ADD COLUMN owner              TEXT NULL;
ALTER TABLE devices ADD COLUMN expected_msg_rate  INTEGER NULL;   -- messages/minute; NULL = no expectation
ALTER TABLE devices ADD COLUMN heartbeat_minutes  INTEGER NULL;   -- alert if silent this long (Phase 8 consumes)
ALTER TABLE devices ADD COLUMN is_enabled         INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1));
ALTER TABLE devices ADD COLUMN approval_status    TEXT NOT NULL DEFAULT 'approved'
                                                  CHECK (approval_status IN ('approved', 'pending', 'rejected'));
ALTER TABLE devices ADD COLUMN approved_utc       TEXT NULL;
ALTER TABLE devices ADD COLUMN approved_by        TEXT NULL;

-- Partial index over just the approval queue (pending/rejected are rare).
CREATE INDEX ix_devices_approval ON devices (approval_status) WHERE approval_status <> 'approved';

------------------------------------------------------------------------------
-- Source IP → device mapping (a device may have several IPs; an IP is one device)
------------------------------------------------------------------------------
CREATE TABLE device_ips (
    device_id   INTEGER NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
    ip          TEXT NOT NULL,
    is_primary  INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
    added_utc   TEXT NOT NULL,
    PRIMARY KEY (device_id, ip)
) WITHOUT ROWID;

-- The uniqueness that makes discovery idempotent: INSERT OR IGNORE on this key means one
-- unknown source IP produces exactly one device record, no matter how many messages arrive.
CREATE UNIQUE INDEX ux_device_ips_ip ON device_ips (ip);

-- Backfill existing devices that already carry a primary_ip.
INSERT OR IGNORE INTO device_ips (device_id, ip, is_primary, added_utc)
    SELECT device_id, primary_ip, 1, created_utc
    FROM devices
    WHERE primary_ip IS NOT NULL AND primary_ip <> '';

------------------------------------------------------------------------------
-- Discovery policy (PHASE_06 build item 2) — single row
------------------------------------------------------------------------------
CREATE TABLE discovery_settings (
    id                     INTEGER PRIMARY KEY CHECK (id = 1),
    unknown_source_policy  TEXT NOT NULL DEFAULT 'auto_register'
                           CHECK (unknown_source_policy IN ('auto_register', 'as_unknown', 'reject')),
    max_pending_devices    INTEGER NOT NULL DEFAULT 500 CHECK (max_pending_devices > 0),
    updated_utc            TEXT NULL,
    updated_by             TEXT NULL
);
INSERT INTO discovery_settings (id) VALUES (1);

------------------------------------------------------------------------------
-- Streams — structured match rules (PHASE_06 build item 6). match_json (001) holds a
-- ConditionNode tree; is_catch_all marks "All Messages".
------------------------------------------------------------------------------
ALTER TABLE streams ADD COLUMN is_catch_all  INTEGER NOT NULL DEFAULT 0 CHECK (is_catch_all IN (0, 1));
ALTER TABLE streams ADD COLUMN updated_utc   TEXT NULL;
ALTER TABLE streams ADD COLUMN updated_by    TEXT NULL;

CREATE INDEX ix_streams_enabled ON streams (enabled) WHERE enabled = 1;
