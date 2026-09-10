# Phase 8 — known issues

## New this phase

### P8-1 — "would have fired" preview is exact only for the SQL fast path
`AlertAdminService.PreviewAsync` returns an **exact** count for a threshold alert with no
`ConditionGroup` filter and an SQL-groupable field (the common case — one bucketed
aggregate). For a *filtered*, *distinct-count*, or *absence* alert it **samples** 24
evenly-spaced windows over the look-back and extrapolates, labelling the result
"approximately N times". The evaluation itself is always exact; only the historical preview
is sampled. Not a correctness issue — target: refine to a full replay if operators ask.

### P8-2 — DeviceSilent live-daemon scenario carried to Phase 12
The phase's manual scenario ("send traffic from a fake device, stop it, confirm the alert
fires and the notification arrives") is reproduced deterministically on a virtual clock
(`AlertDeviceSilentTests`), because the dev VM has no `rsyslogd` (`dev-vm-constraints`,
same class as P3-1). A live-daemon run joins the Phase 12 checklist.

### P8-3 — scheduled-evaluation timing not benchmarked at scale
Alert evaluation runs off the ingest path, so the 5,000 msg/sec ingest gate is unaffected
(no ingest code changed — asserted in `verification.md`). The cost of one filtered-window
scan over a 2M-event DB, and of a tick over dozens of alerts, is bounded by
`Alerts:MaxWindowScan` (500k rows) but not measured on clean-VM hardware. Carried to the
Phase 12 acceptance run alongside P5-1 / P6-1 / P7-4.

## Carried from earlier phases (unchanged)

- **P3-3** — Stryker mutation run (compensated here by the 5,000-case evaluator oracle +
  the SQL/in-memory fetch differential + the 12-scenario labelled FP/FN set).
- **P4-1 / P4-2** — OWASP ZAP DAST, axe-core a11y (no browser on the dev VM; the UX gate's
  five points were done against rendered HTML + source + `WebApplicationFactory` route
  tests).
- **P7-3** — `WriteToOdbc` live round-trip (Phase 12); the alert path reuses the same
  Phase 7 executors unchanged.
- **P2-5 / P7-5** — load-dependent WAL / Argon2 timing flakes in pre-existing tests.
- **P5-3** — wire `user_extractors` into the ingest path (still open; alert filters use the
  Phase 6 `ConditionGroup` which already resolves `field.<name>` extracted fields).
