# Phase 8 — verification (phase prompt "Verification" section)

## `dotnet test --filter "Alert|Schedule|Heartbeat"`

```
Alert compiler / evaluator        AlertCompilerTests, AlertEvaluatorTests,
                                  AlertEvaluatorOracleTests, AlertRuntimeTests,
                                  SearchToConditionTranslatorTests .................. PASS  (46 unit)
Migration 006                     Migration006Tests — alert_definitions / _instances /
                                  _instance_events / _transitions / _eval_runs /
                                  _action_queue; partial UNIQUE (one open instance per
                                  group); state CHECK constraints .................. PASS
Persistence                       AlertPersistenceTests, AlertActionOutboxTests,
                                  AlertWindowReaderTests — round-trip, Version bump,
                                  instance open/ack/resolve/transitions,
                                  claim/fail/backoff/dead-letter/recover/purge,
                                  SQL-aggregate == in-memory grouping .............. PASS
Dedup                             AlertEvaluationServiceTests.Dedup_* — 10 consecutive
                                  evaluations of a true condition -> ONE open instance,
                                  ONE alert.fired audit row ....................... PASS
Re-notify                         AlertReNotifyTests — no re-notify before the interval,
                                  exactly one after ............................... PASS
Device silent + heartbeat         AlertDeviceSilentTests — device stops -> fires after
                                  the heartbeat (within +/- one interval) -> device
                                  resumes -> auto-resolves within one interval;
                                  each device's own heartbeat_minutes honoured ..... PASS
Restart / crash                   AlertRestartTests — restart after a firing does not
                                  double-fire; a 40-minute outage is caught up, the
                                  missed run audited not skipped; 4 crash points in
                                  reconcile all converge to one open instance ...... PASS
Lifecycle + authz                 AlertWebTests — ack/resolve recorded with actor and
                                  audited; Read-Only and Auditor refused at the
                                  service ......................................... PASS
Storm containment                 AlertStormContainmentTests — 40,000-event flood ->
                                  1 firing / 1 notification; 20 groups breaching in one
                                  tick -> budget sends 3, collapses the rest to a
                                  single summary .................................. PASS
Scope leak                        AlertSecurityTests — a triggering event in a stream the
                                  viewer cannot see is not disclosed (no id oracle,
                                  no message body) ................................ PASS
XSS / injection                   AlertSecurityTests — hostile alert fields stored
                                  byte-identical; template substitution is literal, not
                                  evaluation; CR/LF stripped from templated subject/body  PASS
Dispatcher                        AlertActionDispatchServiceTests — claim -> synthetic
                                  event -> execute -> audit; transient -> back-off ->
                                  retry -> dead-letter -> operator notification ..... PASS
Web surface                       AlertWebTests — routes 302 -> /login; role at the
                                  service; preview exact against fixture; device-silent
                                  one-click idempotent; promote-from-saved-search ... PASS
False positive / negative         AlertFalseRateTests — 12 hand-labelled scenarios,
                                  FP rate 0.0%, FN rate 0.0% (fp-fn-rates.md) ...... PASS
```

Filtered run: `test-output-filtered.txt` — **unit 46 / integration 67, 0 failed** (`--filter Alert`).
Full suite: unit **712 / 712**. Integration **494 / 494**.

## Manual scenario (phase prompt)

> "Send traffic from a fake device, stop it, confirm the device-silent alert fires and the
> notification arrives."

Reproduced deterministically on a virtual clock in
`AlertDeviceSilentTests.DeviceStopsSending_FiresAfterHeartbeat_ThenResumes_AutoResolves`
(the dev VM has no `rsyslogd`, per `dev-vm-constraints`): a device with `heartbeat_minutes = 30`
sends one heartbeat, then goes quiet; the scheduler ticks every 5 minutes; the alert opens
between minute 30 and 40 (threshold + at most one evaluation interval); a
`RaiseNotification` action is queued and dispatched; the device resumes and the instance
auto-resolves within one interval. A live-daemon run is on the Phase 12 checklist alongside
P3-1.

## Ingest benchmark

**Not re-run this phase** — the ingest throughput benchmark is re-run in Phases 3, 6, 7, and
12 only (TESTING_STANDARDS §5). Alert evaluation runs on the scheduler thread in the
collector host, entirely off the ingest path; `AlertEvaluationService` /
`AlertActionDispatchService` are registered only in `AddCollectorRuntime` and touch no
ingest code. `git diff --stat` for `src/VSoftSol.Syslog.Ingestion/` this phase: **0 files**.
