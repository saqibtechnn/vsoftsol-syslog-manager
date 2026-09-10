# Phase 8 — red / green log

Every new test was observed failing for the correct reason before the implementation
existed (TESTING_STANDARDS §2.1). Slices are checkpointable; the operator may interrupt at
any boundary.

---

## Slice A — Core model + pure evaluator/compiler + saved-search translator

**Scope:** `Core/Alerts/` (`AlertDefinition`, `AlertInstance`, `AlertTransition`,
`AlertEvaluationType`, `AlertState`, `SearchToConditionTranslator`),
`Rules/Alerts/` (`AlertCompiler`, `AlertEvaluator`, `CompiledAlert`, `AlertWindowData`),
`Rules/Rules/RuleActionValidator` (extracted from `RuleCompiler` — shared, no behaviour change).

**RED** — the three logic entrypoints (`AlertCompiler.Compile`, `AlertEvaluator.Evaluate`,
`SearchToConditionTranslator.Translate`) stubbed to `throw new NotImplementedException`:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Alerts"
Failed!  - Failed:    40, Passed:     0, Skipped:     0, Total:    40

  every test → System.NotImplementedException : phase-08 slice A RED: <entrypoint>
    AlertCompilerTests                     (16) — AlertCompiler.Compile
    AlertEvaluatorTests                    (13) — AlertEvaluator.Evaluate
    AlertEvaluatorOracleTests               (1) — AlertEvaluator.Evaluate
    SearchToConditionTranslatorTests       (10) — SearchToConditionTranslator.Translate
```

Regression check before implementing — the `RuleActionValidator` extraction did not move
any Phase 7 assertion:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Rules"
Passed!  - Failed: 0, Passed: 35, Skipped: 0
```

**GREEN** — implementations restored:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Alerts"
Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40

  AlertEvaluatorOracleTests: 5000 comparisons, 0 divergences
```

No GREEN-phase code fix was needed in slice A.

---

## Slice B — migration 006 + Data stores + window reader + templates

**Scope:** `Data/Migrations/Scripts/006_alerts.sql`, `Data/Alerts/`
(`SqliteAlertStore`, `SqliteAlertInstanceStore`, `SqliteAlertActionOutbox`,
`SqliteAlertWindowReader`, `AlertJson`), `Data/Seed/DefaultAlertTemplates` (8 templates),
`AuditActions` (+11 alert verbs), `DataServiceCollectionExtensions`.

**RED** — migration 006 held back (`006_alerts.sql` → `.sql.hold`), clean rebuild:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Alerts"
Failed!  - Failed:    16, Passed:     6, Skipped:     0, Total:    22

  Migration006Tests      — tables do not exist
  AlertPersistenceTests  — SqliteException: no such table: alert_definitions
  AlertActionOutboxTests  — no such table: alert_action_queue
  AlertWindowReaderTests  — CountByGroup / DistinctCount / DeviceLastSeen return nothing
  (the 6 "passing" assert a throw and get one for a related reason — re-verified green below)
```

Migration count-driven regression check (auto-adapts):

```
dotnet test ... --filter "FullyQualifiedName~Migration"   → Passed: 16
```

**GREEN** — `006_alerts.sql` restored:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Alerts"
Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22

  AlertWindowReaderTests.CountByGroup_AgreesWithInMemoryGrouping — SQL GROUP BY == in-memory grouping
```

GREEN-phase fix: `AlertPersistenceTests.InstanceStore_OpenIsIdempotent` seeded real events
first — `alert_instance_events.event_id` has a FK to `events`, so trigger ids must be real
(they always are in production — they come from the window reader). Test data only; no
production code changed.

---

## Slice C — scheduler + alert runtime + alert action dispatcher + wiring

**Scope:** `Rules/Alerts/` (`AlertRuntime`, `AlertGrouping`), `Service/Hosting/`
(`AlertSetProvider`, `AlertEvaluationOptions`, `AlertEvaluationService`,
`AlertActionDispatchService`), `SqliteAlertStore` (+ `alert_eval_runs` checkpoint),
`SyslogPlatformExtensions` wiring; `TestSupport/AlertEvaluationHarness`.

**RED** — `AlertRuntime.Reserve`, `AlertEvaluationService.TickAsync`,
`AlertActionDispatchService.PassAsync` stubbed to throw:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Alerts.AlertRuntime"
Failed!  - Failed: 6, Passed: 0   (all: NotImplementedException phase-08 slice C RED: AlertRuntime.Reserve)

dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "<the 7 slice-C classes>"
Failed!  - Failed: 26, Passed: 0
  AlertEvaluationServiceTests / AlertDeviceSilentTests / AlertReNotifyTests /
  AlertRestartTests / AlertStormContainmentTests / AlertActionDispatchServiceTests /
  AlertBoundaryMatrixTests → NotImplementedException phase-08 slice C RED: <entrypoint>
```

**GREEN** — implementations restored:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Alerts"       → Passed: 46
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Alerts" → Passed: 48

  AlertEvaluationServiceTests.TimeTravel_ThirtyDays_ProducesTheExactFiringCount — 720 ticks, exactly 720 firings
  AlertStormContainmentTests — 40,000-event flood → 1 firing / 1 notification; 20 simultaneous breaches → 3 sent + summary
  AlertRestartTests.CrashAtVariousPointsInReconcile — 4 crash points, all converge to one open instance
  AlertBoundaryMatrixTests — threshold ±1, window edges, midnight UTC, DST, out-of-order arrival
```

GREEN-phase fixes (test-side only): (a) window-edge tests use `TickAsync()` directly — the
`AdvanceAndTickAsync(1s)` idiom shifts the window by a second, which matters at the boundary;
(b) `> N` threshold semantics ("exactly at N does not fire") — several tests set the threshold
one below the in-window count; (c) `PurgedInstance` test replaced with `UnparseablePayload`
(deleting an instance cascades its queue rows, so the "orphan queue row" scenario the rule
outbox has does not exist for alerts). No production code changed in the GREEN phase.

---

## Slice D — Web: pages, `AlertAdminService`, preview, ack/resolve, one-click, notification centre

**Scope:** `Web/Alerts/AlertAdminService`, `Components/Pages/Alerts/` (`Alerts`, `AlertEditor`,
`AlertTemplates`, `AlertHistory`), `DeviceDetail` one-click, `NotificationCenter` open-alerts
section, `ds.css` Phase 8 block, `WebSecurityExtensions` registration;
`SqliteAlertWindowReader.PreviewBucketCountsAsync`; the old `/alerts` `ComingSoon` removed.

**RED** — `AlertAdminService` mutation / lookup entrypoints stubbed to throw
(`SaveAsync`, `TransitionAsync`, `PreviewAsync`, `GetInstanceAsync`, `PromoteFromSavedSearchAsync`,
`CreateDeviceSilentAsync`):

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "AlertWebTests|AlertSecurityTests"
Failed!  - Failed: 9, Passed: 7, Total: 16   (route-auth theory cases + the store-level XSS test stay green — structural)
```

**GREEN** — implementations restored:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Alerts"
Passed!  - Failed: 0, Passed: 64, Total: 64

  AlertWebTests — route auth (302 → /login), role-at-service, ReadOnly/Auditor cannot ack/resolve,
                  ack/resolve audited with the true actor, preview exact against fixture (3 buckets),
                  device-silent one-click idempotent, promote-from-saved-search prefills a filter
  AlertSecurityTests — triggering events outside the viewer's scope are not disclosed;
                       hostile alert fields stored byte-identical, rendered as literal template text;
                       CR/LF stripped from templated subject/body (no folded-header injection)
```

GREEN-phase code fix (kept, with test): `PreviewBucketCountsAsync` bucketed on
`julianday()` float arithmetic, which rounded `779.9999 → 12` at a 60-second boundary and
split a batch across two buckets. Switched to integer `strftime('%s')` seconds — the bucket
index is now exact. A boundary-aligned fixture in `AlertWebTests` locks this in.

---

## Slice E — evidence, labelled FP/FN set, ADR 0016, THREAT_MODEL / ASVS / SECURITY_REVIEW, PROGRESS, tag

**Scope:** `docs/evidence/phase-08/` (verification, fp-fn-rates, boundary-matrix,
crash-point-results, storm-containment, heartbeat-accuracy, oracle-divergence, known-issues,
ux-gate, security/README, coverage-summary, test-output-*),
`tests/fixtures/alerts/labelled-scenarios.json` + `AlertFalseRateTests`,
`docs/adr/0016-scheduled-alerts-and-alert-outbox.md`, THREAT_MODEL B4 addendum + 4 alert
rows, ASVS Phase 8 pass, SECURITY_REVIEW Phase 8 carry, PROGRESS §9 sign-off.

**RED** — `AlertFalseRateTests` fails before the fixture ships:

```
FileNotFoundException — tests/fixtures/alerts/labelled-scenarios.json
```
then, with the fixture but the evaluator stubbed (from Slice C RED), all 6 FIRE scenarios
report FN and all 6 NO-FIRE scenarios report TN → `FN.Should().Be(0)` fails.

**GREEN**:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Alert"
Passed!  - Failed: 0, Passed: 67

  AlertFalseRateTests: scenarios=12 TP=6 TN=6 FP=0 FN=0
    false-positive rate = 0.0%
    false-negative rate = 0.0%
```

`dotnet format --verify-no-changes` → exit 0 (a `dotnet format` fix pass reindented the
generated-style `switch` blocks in the new test files; no semantic change).

Full suite: unit **712 / 712**, integration **494 / 494**. Build warning-clean, SCA clean
(no new dependency).
