# PHASE 8 — Aggregation Alerts

## Context
Phase 7 reacts to single messages. Real incidents are patterns: five failed logins in
two minutes, or a switch that has gone quiet. This phase adds scheduled evaluators over
windows of data.

## Objective
Threshold, distinct-count, and device-silent alert definitions with a full
acknowledge/resolve workflow.

## Build
1. **Alert definition model**: name, description, severity, filter, evaluation type,
   window, schedule, grouping field, threshold, remediation notes, action list (reuse
   Phase 7 actions), enabled flag.
2. **Evaluation types**:
   - `Threshold` — count of matching events grouped by field exceeds N within window
   - `DistinctCount` — distinct values of a field exceeds N within window
   - `DeviceSilent` — no events received from a device (or device group) for N minutes.
     **This is a headline differentiator** — it detects a dead switch, a broken syslog
     config, or a failed collector path. Honour each device's own heartbeat threshold.
   - `Absence` — no events matching a filter within window (e.g. no nightly backup log)
3. **Scheduler** — quartz-style or hosted timer, evaluations must not overlap themselves,
   missed runs are logged not silently skipped, and evaluation state survives restart.
4. **Deduplication**: an alert in the firing state does not re-fire every evaluation.
   It fires once, stays open, and re-notifies only on a configurable re-notify interval.
5. **Alert lifecycle**: `Firing → Acknowledged → Resolved`, with actor, timestamp, and
   free-text note at each transition. Auto-resolve when the condition clears, configurable.
6. **Alert history** page: filterable, showing full lifecycle and the events that
   triggered each firing (store the triggering event IDs, not a copy of the events).
7. Promote-to-alert from a saved search (the Phase 5 hook).
8. Notification centre in the shell shows open alerts by severity.

## Do not build in this phase
Dashboard widgets showing alerts — that is Phase 9.

## Tests to write first
Use a virtual clock throughout; no `Thread.Sleep` in tests.
- Threshold test: exactly at N does not fire, N+1 fires, and it fires **once** per window.
- Dedup test: a condition true for 10 consecutive evaluations produces one open alert.
- Re-notify test: honours the configured interval.
- DeviceSilent test: device stops sending → alert fires after the threshold → device
  resumes → alert auto-resolves.
- Restart test: kill mid-schedule, restart, assert no duplicate fire and no missed window.
- Lifecycle test: ack and resolve transitions recorded with actor and audited.

## Verification — run these and paste output
```bash
dotnet test --filter "Alert|Schedule|Heartbeat"
```
Then run a manual scenario: send traffic from a fake device, stop it, confirm the
device-silent alert fires and the notification arrives.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task: **set up an alert for when a core switch stops
sending logs.** Device-silent is the differentiator, so it must be the easiest alert in
the product to create — offer it as a one-click action directly from the device health
card, not only from a generic alert-builder page. Ship 6-8 alert templates. Every alert
definition shows a live preview: "this would have fired N times in the last 7 days" so
users can tune the threshold before saving instead of discovering it by being spammed.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Virtual clock time-travel suite** — no test may take longer than the code under test.
  Simulate 30 days of evaluations in seconds and assert exact firing counts.
- **Labelled dataset for false positive/negative rate** — construct a fixture dataset
  with known-correct expected alerts, then measure. State the measured FP and FN rates in
  the evidence pack. An alerting system nobody has measured is an alerting system nobody
  will trust.
- **Boundary matrix** — exactly at threshold, one below, one above, at window edges,
  across DST transitions, across midnight UTC, and with events arriving out of order.
- **Crash-during-evaluation** — kill at 10 points in the evaluation cycle; restart; assert
  no duplicate fire, no missed window, and no stuck-open alert.
- **Storm containment** — inject 100k matching events in 10 s; assert the alert fires once
  and the global action budget prevents a notification flood.
- **Heartbeat accuracy** — device stops at T; assert the alert fires within the configured
  threshold ± one evaluation interval, and auto-resolves within one interval of resumption.
- **Evidence:** FP/FN rates against the labelled set, boundary matrix, crash-point results
  (10/10), storm containment log.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Notification flooding as an attack** — an attacker who can generate log events can
  weaponise the alerting system against the administrator's inbox or a downstream
  webhook. Assert the global action budget and per-alert cool-down contain it, and that
  the collapse-to-summary path triggers.
- **Information disclosure in alert content** — alert emails and webhooks must respect
  the recipient's scope. A notification must never leak an event from a stream the
  recipient cannot see.
- **Stored XSS in alert fields** — descriptions, remediation notes, and triggering-event
  previews render in the notification centre and in emails. Test HTML email escaping
  specifically; it is a commonly missed surface.
- **Authorization on lifecycle** — assert Read-Only cannot acknowledge or resolve, and
  that ack/resolve actions are audited with the true actor.
- **Evidence:** flood containment results, scope-leak matrix, XSS results including
  HTML email.

## Definition of Done
Standard DoD, plus the restart-safety test passes and the threshold preview is accurate
against fixture data.

## Commit
`feat: phase 8 — aggregation alerts, device-silent heartbeat, ack/resolve` → tag `v1.0.0-phase.8`
