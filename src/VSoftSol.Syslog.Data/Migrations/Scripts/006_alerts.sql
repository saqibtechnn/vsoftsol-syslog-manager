-- Migration 006 — Phase 8: aggregation alerts. Forward-only, one transaction.
-- Scheduled evaluators over windows of data (threshold / distinct-count / device-silent /
-- absence) with a Firing → Acknowledged → Resolved lifecycle, a crash-safe action outbox
-- of the same shape as the Phase 7 rule outbox, and an evaluation checkpoint so a restart
-- neither double-fires nor silently skips a window (ADR 0016).

------------------------------------------------------------------------------
-- Alert definitions (PHASE_08 build item 1)
------------------------------------------------------------------------------
CREATE TABLE alert_definitions (
    alert_id           INTEGER PRIMARY KEY,
    name               TEXT NOT NULL UNIQUE,
    description         TEXT NULL,
    severity           TEXT NOT NULL DEFAULT 'warning' CHECK (severity IN ('info', 'warning', 'critical')),
    enabled            INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    eval_type          TEXT NOT NULL CHECK (eval_type IN ('threshold', 'distinct_count', 'device_silent', 'absence')),
    filter_json        TEXT NULL,                        -- ConditionGroup JSON, or NULL for "everything"
    window_seconds     INTEGER NOT NULL CHECK (window_seconds > 0),
    interval_seconds   INTEGER NOT NULL CHECK (interval_seconds > 0),
    group_by_field     TEXT NULL,
    threshold          INTEGER NOT NULL DEFAULT 0,
    remediation_notes  TEXT NULL,
    actions_json       TEXT NULL,                        -- RuleAction[] JSON (never a secret VALUE)
    device_group_ids   TEXT NULL,                        -- JSON array; NULL/[] = every device
    stream_ids         TEXT NULL,                        -- JSON array; NULL/[] = every stream
    renotify_seconds   INTEGER NOT NULL DEFAULT 3600 CHECK (renotify_seconds >= 0),
    auto_resolve       INTEGER NOT NULL DEFAULT 1 CHECK (auto_resolve IN (0, 1)),
    is_system          INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1)),
    hit_count          INTEGER NOT NULL DEFAULT 0,
    last_evaluated_utc TEXT NULL,
    last_fired_utc     TEXT NULL,
    created_utc        TEXT NOT NULL,
    updated_utc        TEXT NOT NULL,
    updated_by         TEXT NULL
);

CREATE INDEX ix_alert_definitions_enabled ON alert_definitions (alert_id) WHERE enabled = 1;

------------------------------------------------------------------------------
-- Alert instances — one per firing (PHASE_08 build items 5 & 6)
------------------------------------------------------------------------------
CREATE TABLE alert_instances (
    instance_id        INTEGER PRIMARY KEY,
    alert_id           INTEGER NOT NULL REFERENCES alert_definitions(alert_id) ON DELETE CASCADE,
    group_value        TEXT NOT NULL DEFAULT '',         -- '' for an ungrouped alert; NOT NULL so the partial UNIQUE works
    state              TEXT NOT NULL DEFAULT 'firing' CHECK (state IN ('firing', 'acknowledged', 'resolved')),
    severity           TEXT NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    observed_value     INTEGER NOT NULL,
    threshold          INTEGER NOT NULL,
    opened_utc         TEXT NOT NULL,
    acknowledged_utc   TEXT NULL,
    acknowledged_by    TEXT NULL,
    resolved_utc       TEXT NULL,
    resolved_by        TEXT NULL,
    auto_resolved      INTEGER NOT NULL DEFAULT 0 CHECK (auto_resolved IN (0, 1)),
    last_notified_utc  TEXT NULL,
    note               TEXT NULL
);

-- Deduplication: at most one non-resolved instance per (alert, group). A condition that
-- stays true across many evaluations re-notifies the same instance, never opens a new one.
CREATE UNIQUE INDEX ux_alert_instances_open ON alert_instances (alert_id, group_value) WHERE state <> 'resolved';
CREATE INDEX ix_alert_instances_open  ON alert_instances (opened_utc DESC) WHERE state <> 'resolved';
CREATE INDEX ix_alert_instances_alert ON alert_instances (alert_id, opened_utc DESC);

-- The triggering events — stored as ids, never a copy of the events (PHASE_08 item 6).
CREATE TABLE alert_instance_events (
    instance_id  INTEGER NOT NULL REFERENCES alert_instances(instance_id) ON DELETE CASCADE,
    event_id     INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    PRIMARY KEY (instance_id, event_id)
) WITHOUT ROWID;

-- Lifecycle history — actor, timestamp, and free-text note at each transition (PHASE_08 item 5).
CREATE TABLE alert_transitions (
    transition_id  INTEGER PRIMARY KEY,
    instance_id    INTEGER NOT NULL REFERENCES alert_instances(instance_id) ON DELETE CASCADE,
    from_state     TEXT NULL CHECK (from_state IN ('firing', 'acknowledged', 'resolved')),
    to_state       TEXT NOT NULL CHECK (to_state IN ('firing', 'acknowledged', 'resolved')),
    actor          TEXT NOT NULL,
    note           TEXT NULL,
    occurred_utc   TEXT NOT NULL
);

CREATE INDEX ix_alert_transitions_instance ON alert_transitions (instance_id, transition_id);

------------------------------------------------------------------------------
-- Evaluation checkpoint — survives restart; a missed run is logged, not skipped (item 3)
------------------------------------------------------------------------------
CREATE TABLE alert_eval_runs (
    alert_id             INTEGER PRIMARY KEY REFERENCES alert_definitions(alert_id) ON DELETE CASCADE,
    last_window_end_utc  TEXT NULL,
    last_run_utc         TEXT NULL,
    last_status          TEXT NULL,
    consecutive_failures INTEGER NOT NULL DEFAULT 0
);

------------------------------------------------------------------------------
-- Alert action outbox — the same crash-safe shape as rule_action_queue (ADR 0015/0016).
-- notify_seq: 0 = the opening notification, 1.. = re-notify rounds. The UNIQUE key makes
-- each round idempotent, so a restart mid-dispatch cannot double-send.
------------------------------------------------------------------------------
CREATE TABLE alert_action_queue (
    queue_id         INTEGER PRIMARY KEY,
    alert_id         INTEGER NOT NULL REFERENCES alert_definitions(alert_id) ON DELETE CASCADE,
    instance_id      INTEGER NOT NULL REFERENCES alert_instances(instance_id) ON DELETE CASCADE,
    action_index     INTEGER NOT NULL,
    notify_seq       INTEGER NOT NULL DEFAULT 0,
    kind             TEXT NOT NULL,
    payload_json     TEXT NOT NULL,
    state            TEXT NOT NULL DEFAULT 'pending'
                     CHECK (state IN ('pending', 'running', 'done', 'failed', 'dead')),
    attempts         INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT NOT NULL,
    created_utc      TEXT NOT NULL,
    completed_utc    TEXT NULL,
    last_error       TEXT NULL,
    UNIQUE (instance_id, action_index, notify_seq)
);

CREATE INDEX ix_alert_action_queue_claim ON alert_action_queue (next_attempt_utc)
    WHERE state IN ('pending', 'failed');
CREATE INDEX ix_alert_action_queue_purge ON alert_action_queue (completed_utc)
    WHERE state IN ('done', 'dead');
