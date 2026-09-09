-- Migration 005 — Phase 7: the rules & actions engine. Forward-only, one transaction.
-- The rule model reuses condition_json / actions_json / priority / stop_processing from
-- migration 001; this adds the Phase 7 columns, the crash-safe action outbox, and the
-- UI notification store (ADR 0015).

------------------------------------------------------------------------------
-- Rules — the Phase 7 fields (PHASE_07 build item 1)
------------------------------------------------------------------------------
ALTER TABLE rules ADD COLUMN device_group_ids  TEXT NULL;      -- JSON array of group ids; NULL/[] = all
ALTER TABLE rules ADD COLUMN time_window_json  TEXT NULL;      -- TimeOfDayWindow JSON, or NULL
ALTER TABLE rules ADD COLUMN escalation_json   TEXT NULL;      -- EscalationPolicy JSON, or NULL
ALTER TABLE rules ADD COLUMN hit_count         INTEGER NOT NULL DEFAULT 0;
ALTER TABLE rules ADD COLUMN last_fired_utc    TEXT NULL;
ALTER TABLE rules ADD COLUMN updated_by        TEXT NULL;
ALTER TABLE rules ADD COLUMN is_system         INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1));

-- Ingest evaluates enabled rules in priority order; a partial index over just those.
CREATE INDEX ix_rules_priority ON rules (priority, rule_id) WHERE enabled = 1;

------------------------------------------------------------------------------
-- Action outbox (PHASE_07 items 3-5; ADR 0015). One row per matched side-effecting
-- action, written in the same transaction as its event. A background dispatcher claims
-- rows, executes, retries with back-off, and dead-letters. The UNIQUE key makes a
-- re-evaluated event idempotent — a restart cannot double-fire.
------------------------------------------------------------------------------
CREATE TABLE rule_action_queue (
    queue_id        INTEGER PRIMARY KEY,
    rule_id         INTEGER NOT NULL REFERENCES rules(rule_id) ON DELETE CASCADE,
    action_index    INTEGER NOT NULL,
    event_id        INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    kind            TEXT NOT NULL,
    payload_json    TEXT NOT NULL,                 -- the RuleAction, serialised (never a secret VALUE)
    was_escalation  INTEGER NOT NULL DEFAULT 0 CHECK (was_escalation IN (0, 1)),
    state           TEXT NOT NULL DEFAULT 'pending'
                    CHECK (state IN ('pending', 'running', 'done', 'failed', 'dead')),
    attempts        INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL,
    created_utc     TEXT NOT NULL,
    completed_utc   TEXT NULL,
    last_error      TEXT NULL,
    UNIQUE (rule_id, event_id, action_index)
);

CREATE INDEX ix_action_queue_claim ON rule_action_queue (next_attempt_utc)
    WHERE state IN ('pending', 'failed');
CREATE INDEX ix_action_queue_purge ON rule_action_queue (completed_utc)
    WHERE state IN ('done', 'dead');

------------------------------------------------------------------------------
-- Notifications (PHASE_07 item 3 "RaiseNotification" + the Phase 4 notification centre).
------------------------------------------------------------------------------
CREATE TABLE notifications (
    notification_id INTEGER PRIMARY KEY,
    level           TEXT NOT NULL CHECK (level IN ('info', 'warning', 'critical')),
    title           TEXT NOT NULL,
    body            TEXT NOT NULL DEFAULT '',
    source          TEXT NOT NULL DEFAULT 'rule',   -- 'rule' | 'system'
    rule_id         INTEGER NULL REFERENCES rules(rule_id) ON DELETE SET NULL,
    created_utc     TEXT NOT NULL,
    read_utc        TEXT NULL,
    dismissed_utc   TEXT NULL
);

CREATE INDEX ix_notifications_active ON notifications (created_utc DESC)
    WHERE dismissed_utc IS NULL;
