-- Migration 009 — Phase 11: MFA, Windows Event Log API keys, and config bundle trust.
-- Forward-only, one transaction (MigrationRunner).

------------------------------------------------------------------------------
-- TOTP MFA (PHASE_11 build item 8). The secret itself is never stored here — it goes
-- through the existing DPAPI-protected `secrets` table (SqliteSecretStore, keyed
-- "mfa.totp.<user_id>"), the same mechanism SMTP passwords and webhook tokens already use.
-- These two columns are metadata only: whether MFA is turned on, and when it was enrolled.
------------------------------------------------------------------------------
ALTER TABLE users ADD COLUMN mfa_enabled      INTEGER NOT NULL DEFAULT 0 CHECK (mfa_enabled IN (0, 1));
ALTER TABLE users ADD COLUMN mfa_enrolled_utc TEXT NULL;

CREATE TABLE mfa_recovery_codes (
    id           INTEGER PRIMARY KEY,
    user_id      INTEGER NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    code_hash    TEXT NOT NULL,
    used_utc     TEXT NULL,
    created_utc  TEXT NOT NULL
);
CREATE INDEX ix_mfa_recovery_codes_user ON mfa_recovery_codes (user_id);

------------------------------------------------------------------------------
-- Windows Event Log intake API keys (PHASE_11 build item 3). Only a hash is ever stored —
-- the key is shown once at creation/rotation and never again, the same convention as a
-- recovery code.
------------------------------------------------------------------------------
CREATE TABLE api_keys (
    id                 INTEGER PRIMARY KEY,
    label              TEXT NOT NULL,
    key_hash           TEXT NOT NULL UNIQUE,
    purpose            TEXT NOT NULL,             -- e.g. 'wineventlog'
    allowed_source_ip  TEXT NULL,                 -- NULL = any source; set = the only IP this key accepts from
    created_utc        TEXT NOT NULL,
    created_by         TEXT NULL,
    revoked_utc        TEXT NULL
);
CREATE INDEX ix_api_keys_purpose ON api_keys (purpose) WHERE revoked_utc IS NULL;

------------------------------------------------------------------------------
-- Config bundle signing identity and trust (PHASE_11 build item 4, ADR 0019). This
-- install's own ECDSA key pair signs bundles it exports; the private key is stored through
-- the same DPAPI-protected `secrets` table (name "bundle.signing.privatekey"). Trusted
-- signers are accepted trust-on-first-use — an Administrator explicitly accepts a new
-- signer's fingerprint before its first import is processed, and every later bundle from
-- that same fingerprint is verified without re-prompting.
------------------------------------------------------------------------------
CREATE TABLE bundle_signing_identity (
    id           INTEGER PRIMARY KEY CHECK (id = 1),
    public_key   TEXT NOT NULL,
    fingerprint  TEXT NOT NULL,
    created_utc  TEXT NOT NULL
);

CREATE TABLE bundle_trusted_signers (
    fingerprint  TEXT PRIMARY KEY,
    public_key   TEXT NOT NULL,
    label        TEXT NOT NULL,
    trusted_utc  TEXT NOT NULL,
    trusted_by   TEXT NOT NULL
);

CREATE TABLE bundle_imports (
    id                  INTEGER PRIMARY KEY,
    title               TEXT NOT NULL,
    kind                TEXT NOT NULL,             -- 'vendor_pack' | 'customer_export'
    signer_fingerprint  TEXT NOT NULL,
    sections            TEXT NOT NULL,              -- comma-separated section names imported
    imported_utc        TEXT NOT NULL,
    imported_by         TEXT NOT NULL
);
CREATE INDEX ix_bundle_imports_imported_utc ON bundle_imports (imported_utc);
