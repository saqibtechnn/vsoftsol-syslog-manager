-- Migration 002 — Phase 4: authentication, scoped visibility, sessions, secrets.
-- Forward-only. Runs inside one transaction managed by MigrationRunner.
-- Migration 001 is immutable once applied; this file adds only.

------------------------------------------------------------------------------
-- Per-user visibility scope
------------------------------------------------------------------------------
-- A row grants visibility of exactly one stream OR one device group. A user with NO
-- rows here is unrestricted (sees everything) — this matches the AuthenticatedUser
-- contract, where an empty VisibleStreamIds / VisibleDeviceGroupIds means "all".
-- The scope filter (ScopedEventReader) is the single chokepoint that reads this.

CREATE TABLE user_scopes (
    user_id          INTEGER NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    stream_id        INTEGER NULL REFERENCES streams(stream_id) ON DELETE CASCADE,
    device_group_id  INTEGER NULL REFERENCES device_groups(group_id) ON DELETE CASCADE,
    -- exactly one of the two targets is set
    CHECK (((stream_id IS NOT NULL) + (device_group_id IS NOT NULL)) = 1)
);

CREATE INDEX ix_user_scopes_user ON user_scopes (user_id);
CREATE UNIQUE INDEX ux_user_scopes_stream ON user_scopes (user_id, stream_id) WHERE stream_id IS NOT NULL;
CREATE UNIQUE INDEX ux_user_scopes_group  ON user_scopes (user_id, device_group_id) WHERE device_group_id IS NOT NULL;

------------------------------------------------------------------------------
-- Server-side sessions
------------------------------------------------------------------------------
-- The authentication cookie carries only an opaque session id. The server row is
-- authoritative: logout and administrative revocation void it immediately, so a stolen
-- or fixated cookie is dead the moment the row is gone. Idle and absolute timeouts are
-- enforced against these columns, not against cookie lifetime alone.

CREATE TABLE user_sessions (
    session_id           TEXT PRIMARY KEY,      -- 256-bit CSPRNG, base64url, server-issued only
    user_id              INTEGER NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    created_utc          TEXT NOT NULL,
    last_seen_utc        TEXT NOT NULL,
    absolute_expiry_utc  TEXT NOT NULL,
    source_ip            TEXT NULL,
    user_agent           TEXT NULL,
    revoked_utc          TEXT NULL
);

CREATE INDEX ix_user_sessions_user ON user_sessions (user_id);
CREATE INDEX ix_user_sessions_expiry ON user_sessions (absolute_expiry_utc);

------------------------------------------------------------------------------
-- DPAPI-protected secrets
------------------------------------------------------------------------------
-- SMTP passwords, webhook tokens, ODBC strings. The stored value is ciphertext produced
-- by ProtectedData (DPAPI). Plaintext never reaches the database, the logs, the audit
-- diff, or a config-bundle export (SECURITY_STANDARDS.md §5.5). Consumed from Phase 7.

CREATE TABLE secrets (
    name             TEXT PRIMARY KEY,
    protected_value  BLOB NOT NULL,
    updated_utc      TEXT NOT NULL,
    updated_by       TEXT NULL
);

------------------------------------------------------------------------------
-- users — password lifecycle
------------------------------------------------------------------------------

ALTER TABLE users ADD COLUMN password_changed_utc TEXT NULL;

------------------------------------------------------------------------------
-- audit_log — tamper evidence (defence in depth on top of the 001 triggers)
------------------------------------------------------------------------------
-- The triggers block UPDATE/DELETE through any SQL path. The hash chain additionally
-- makes out-of-band tampering (a process with direct file access, restore of a doctored
-- file) detectable: entry_hash = SHA-256( prev_hash || canonical(row) ), verified by
-- SqliteAuditLog.VerifyChainAsync. Retro-editing one row breaks every later link.

ALTER TABLE audit_log ADD COLUMN prev_hash  TEXT NULL;
ALTER TABLE audit_log ADD COLUMN entry_hash TEXT NULL;
