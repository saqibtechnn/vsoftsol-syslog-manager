# Phase 10 — known issues

Per TESTING_STANDARDS.md §6 (defect protocol): every bug found is logged with root cause,
a reproducing test, and a fix — kept permanently, never removed.

## Bugs found and fixed during this phase (test-first, per §6)

| ID | Root cause | Reproducing test | Fix |
|---|---|---|---|
| B10-1 | `RestoreArchiveAsync` / `ExpireRestoresBatchAsync` acquired the connection factory's non-reentrant write-lock `SemaphoreSlim` via `await using` at method/block scope, then called a store method that acquired the *same* semaphore again before the outer scope released it — a self-deadlock. Found live: the first full Retention integration run hung indefinitely (confirmed via process age, ~13 min vs. the ~8 s a healthy run takes) rather than failing fast. | `RestoreTests`, any test exercising restore or restore-expiry (the whole suite hung, not one test) | Narrowed the `await using` to an explicit block that disposes (releases the lock) before the second call, in both methods. |
| B10-2 | `SqliteArchiveStore.ListForPurgeAsync` wrapped `COALESCE` *inside* a correlated scalar subquery (`SELECT COALESCE(rp.cold_days, $default) FROM retention_policies rp WHERE rp.stream_id = a.stream_id`). When `a.stream_id IS NULL` (an archive with no primary stream) the subquery matches zero rows and evaluates to SQL `NULL` as a whole — the outer `COALESCE` never runs, so the purge-horizon comparison against `NULL` is never true and the archive is never purged. | `SqliteRetentionEngineTests.PurgeExpiredArchivesBatchAsync_DeletesTheFile_AndMarksTheRowDeleted` | Moved `COALESCE` to wrap the whole subquery: `COALESCE((SELECT rp.cold_days FROM …), $default)`. |
| B10-3 | The query grammar's match-all convention is an empty string, not `"*"` — a bare `"*"` is a prefix-wildcard operator with nothing to prefix and is rejected by `SearchQueryParser` ("A wildcard (*) needs at least one character before it"). Three canned templates, the `ReportTemplate.QueryText` default, `ReportContentReader`'s custom-report fallback, and the editor page's default all used the literal `"*"`. Every affected aggregation report silently returned an *empty* result (`AggregationStatus.BadQuery`) because the caller did not check `AggregationOutcome.Status` — a report ran to "completion" showing zero rows, no error surfaced anywhere. | `ReportContentReaderTests.SeverityTrend_MatchesAnIndependentSqlOracle_PerSeverity` (oracle said 3, product said 0), `FailedAuthenticationSummary_MatchesAnIndependentSqlOracle` | Every occurrence changed to `string.Empty`; added `CannedReportCatalogTests.NoTemplate_UsesABareWildcardForMatchAll` as a permanent regression guard at the unit level (catches the mistake without needing to execute a real query). |
| B10-4 | `DeviceAvailability` and `TopTalkers` grouped by the token `"host"`, which is not in `EventColumns.Groupable` (the allow-listed key is `"hostname"`, matching the real column). `AggregationCompiler` rejected the spec — again a silent empty result, same blast radius as B10-3. | Covered by `ResolveAsync_EveryCatalogueTemplate_ResolvesWithoutError` once combined with a content assertion (row count) — caught during B10-3 triage, same run. | Changed both `GroupByField` values to `"hostname"`. |

**Pattern across B10-3 and B10-4**: `AggregationOutcome.Status` is available on every call
but `ReportContentReader.ResolveAsync` doesn't surface it — a broken aggregation currently
looks identical to "no data in range" to the report's reader (both are an empty
`AggregateRows`). This is real and not fully closed by this phase's fixes (the fixes
correct the two known-bad specs; they don't add status surfacing). Deferred:

- [ ] **P10-2** — `ReportContentReader` should propagate `AggregationOutcome.Status`/`Detail`
  into `ReportContent` (e.g. a nullable `Error` property) so a genuinely malformed custom
  report's query shows a plain-English reason instead of a silent empty report — target:
  the report editor's "Run now" flow, Phase 11 or later Reports polish pass.

## Environmental carries (not bugs — see SECURITY_REVIEW.md for the operator sign-off table)

- **P10-1** — the phase's literal "10M-event backlog, search latency unaffected while
  tiering runs" acceptance is carried to the Phase 12 clean-VM run, the same disposition
  as P1-1 / P5-1 / P6-1 / P9-1. `RetentionBenchmark` measures real Hot→Warm throughput at
  200k events on this 2-vCPU VM instead.
- **P3-3** (Stryker mutation testing) applies to the retention logic in this phase the same
  as every prior phase — the VsTest adapter does not deploy on this SDK-only host.
- **P4-1 / P4-2** (OWASP ZAP DAST, axe-core) — no browser on this host; compensating xUnit
  assertions against real Kestrel, per the standing precedent.

## Design simplifications (stated, not defects)

- **Primary-stream retention** (ADR 0018 decision 1): an event's retention policy comes
  from its most specific non-catch-all stream, not the most conservative policy across
  every stream it belongs to. A deliberate scope decision, not a bug — documented in the
  ADR with the alternative considered and why it was rejected.
- **PDF/CSV figure correctness** is proved at the `ReportContent`-vs-independent-SQL-oracle
  layer, not by parsing the rendered PDF back out (QuestPDF has no reader API). The
  renderer tests prove fidelity to already-verified content (no throw, scales with row
  count, hostile content stays literal text / formula-guarded) rather than re-deriving the
  figures from the rendered bytes.
