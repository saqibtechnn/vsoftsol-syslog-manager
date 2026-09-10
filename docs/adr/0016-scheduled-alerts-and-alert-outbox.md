# ADR 0016 — Aggregation alerts: scheduled evaluation, a hybrid window reader, and a second action outbox

**Status:** Accepted (Phase 8)

## Context

PHASE_07 reacts to a single message. PHASE_08 adds evaluators over *windows* of data on a
schedule — a burst of failures, an unusual number of distinct sources, a device that has
gone quiet, the absence of an expected log. Constraints:

- **CLAUDE.md "Repository layout"** — `Core` does no I/O; `Rules` owns evaluation logic;
  `Data` owns SQLite; only `Service` may bridge `Data` + `Rules`.
- **CLAUDE.md "Two seams only"** — `ILogRepository`, `IAuthenticationProvider`. No new seam.
- **CLAUDE.md Constraint 2** — no external runtime dependency (rules out Quartz/Hangfire).
- **PHASE_08** — evaluations must not overlap themselves; a missed run is logged, not
  skipped; evaluation state survives restart; a firing alert does not re-fire every
  evaluation (dedup + a re-notify interval); `Firing → Acknowledged → Resolved` with actor,
  timestamp, and note at each transition; auto-resolve when the condition clears.
- **SECURITY_STANDARDS.md / PHASE_08 security** — an attacker who can generate log events
  must not weaponise alerting against the admin's inbox; a notification must not surface an
  event from a stream the viewer cannot see.
- **ADR 0005** — one process, so an in-memory version counter is authoritative.
- **ADR 0014 / 0015** — reuse the polymorphic `ConditionGroup` filter, the Phase 7
  `RuleAction` model, the `RuleCompiler` action validation, the version-counter provider,
  and the crash-safe outbox pattern.

## Decision

### 1. Rule evaluation is per-message on the ingest path; alert evaluation is scheduled, off it.

`AlertEvaluationService : BackgroundService` (collector host only) ticks every
`Alerts:TickInterval` (15 s default). Each tick: for every enabled alert whose interval has
elapsed since the persisted `alert_eval_runs` checkpoint, fetch the window data, evaluate,
reconcile `alert_instances`. Ticks never overlap (the loop is `tick; await delay`); within a
tick, due alerts evaluate in parallel bounded by `Alerts:DispatchParallelism`. A run overdue
by more than `MissedRunGraceMultiplier × interval` (the process was down) writes an
`alert.evaluation.missed` audit row and catches up on the current window — it is never
silently skipped. No Quartz: a hosted timer + a persisted checkpoint is enough for a single
node (Constraint 2).

### 2. The window data is fetched with a hybrid model — SQL aggregate where possible, an in-memory filtered scan otherwise.

`SqliteAlertWindowReader` (in `Data`):

- **No `ConditionGroup` filter** (the common case — "count events from device group X"): a
  parameterised `GROUP BY` straight off the `received_utc` index. Scales to millions of rows;
  the group-by column is resolved against a fixed allow-list, never interpolated.
  `strftime('%s')` integer-seconds bucketing (not `julianday` float) so bucket indices are
  exact for the "would have fired" preview.
- **With a filter**: `StreamWindowAsync` streams the windowed rows (capped at
  `Alerts:MaxWindowScan`, default 500k) and the scheduler applies the compiled
  `ConditionEvaluator` in memory, grouping the survivors. A truncated scan fires on a
  lower-bound count and raises a diagnostic notification; it does **not** auto-resolve.
- **DeviceSilent**: `devices.last_seen_utc` / `MAX(received_utc)` per device, compared
  against each device's own `heartbeat_minutes` (falling back to the alert window).

The differential test (`AlertWindowReaderTests.CountByGroup_AgreesWithInMemoryGrouping`)
proves the SQL aggregate and an in-memory grouping of the same rows produce identical
counts — the two evaluation paths cannot diverge. `AlertGrouping.KeyFor` mirrors the SQL
`COALESCE(col,'(none)')` expression exactly.

### 3. The decision is a pure function; dedup and re-notify are the store's job.

`AlertEvaluator.Evaluate(CompiledAlert, AlertWindowData, DateTimeOffset now) → AlertEvaluation`
is pure (no I/O, no clock but the argument), so every boundary and time-travel case is a
plain unit test and the differential oracle is trivial. The scheduler then reconciles:

- **Dedup** = the partial unique index `ux_alert_instances_open (alert_id, group_value)
  WHERE state <> 'resolved'`. `SqliteAlertInstanceStore.OpenAsync` is `INSERT OR IGNORE`
  then read-back: a condition that stays true across ten evaluations produces exactly one
  open instance and one `alert.fired` audit row.
- **Re-notify** = queue the actions again (with `notify_seq > 0`) only when
  `now - last_notified >= ReNotifySeconds`.
- **Auto-resolve** = for every open instance whose group is no longer breaching, transition
  to `resolved` with `auto_resolved = 1`.

### 4. A second crash-safe outbox — `alert_action_queue` — not a generalised `rule_action_queue`.

Same shape as ADR 0015's `rule_action_queue` (state machine, attempts, `next_attempt_utc`,
back-off, dead-letter, stale-`running` recovery, purge) but keyed on `alert_id` /
`instance_id` and with a `notify_seq` column so the UNIQUE key
`(instance_id, action_index, notify_seq)` makes both a re-notify round and a restart
mid-dispatch idempotent. Written by the scheduler (not the ingest transaction — alerts have
no single triggering message). Drained by `AlertActionDispatchService : BackgroundService`,
which reuses the Phase 7 `ActionExecutorRegistry` and hands each executor a **synthetic**
`SyslogEvent` built from the firing instance (`{hostname}` = group value, `{message}` = a
summary, `{field.alert_*}` = name / value / threshold / group) so the same templating and
CR/LF stripping apply. Phase 7's `rule_action_queue` / `ActionDispatchService` are untouched
— their 84 tests stay green.

### 5. The storm budget lives in `AlertRuntime` (mirrors the throttle half of `RuleRuntime`).

`AlertRuntime.Reserve` — per-action rate limit + cool-down (keyed by alert + action id) and
a global `Alerts:GlobalActionsPerMinute` budget (60 default). Overflow is collapsed and
surfaced once per minute as a single "Alert-storm protection engaged" summary. A 40,000-event
flood fires the alert **once** and sends **one** notification (dedup); 20 groups breaching in
one tick send the budget's worth and collapse the rest.

### 6. Scope on alert content.

`AlertAdminService` filters instance listings by the viewer's `UserScope` (an unrestricted
alert — one that watches everything — is visible to all, its existence is not sensitive) and
routes every triggering-event lookup through `ScopedEventReader.GetByIdAsync`, which returns
null for an out-of-scope id. The history detail shows "#N (outside your visible scope)" with
no message body. Alert *action* recipients are the alert author's choice (an Operator, who
sees everything) — that is a configuration decision, not a per-viewer scope surface.

## Consequences

- **Good:** no new dependency; no new seam; the pure evaluator + differential oracle give
  the mutation-score compensating evidence (P3-3 still blocked); the outbox pattern reuse
  means retry / dead-letter / restart-safety came for free; the hybrid reader keeps the
  common alert cheap while still supporting arbitrary `ConditionGroup` filters.
- **Cost:** a second outbox table + dispatcher (deliberate — bounded blast radius vs
  generalising the stable Phase 7 path); the filtered-window path is O(rows in window)
  capped at `MaxWindowScan`; the "would have fired" preview is exact only for the SQL path
  and sampled (labelled "approximately") for filtered / distinct / absence alerts.
- **Deferred:** a live SMTP/webhook round-trip for an alert action → covered by the Phase 7
  action executors and their fault-injection suite (unchanged); the 50M-event scheduled
  evaluation timing → Phase 12 clean-VM (alert evaluation is off the ingest path, so the
  5,000 msg/sec gate is unaffected — asserted by "no ingest code changed").
