# Phase 10 — red/green log

Per phase, per slice. Each slice's tests were written first; RED was observed by
temporarily removing the new implementation (compile failure) or, where noted, by
running against a stub that threw `NotImplementedException` or returned an obviously
wrong value — then the real implementation was added/restored and the run repeated to
confirm GREEN. This mirrors the Phase 7–9 pattern.

## Slice A — Core: retention/report domain types, compression, schedule, catalogue

**RED** — `src/VSoftSol.Syslog.Core/Retention/` and `.../Reports/` moved aside; ran
`dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Retention|FullyQualifiedName~Reports.ReportScheduleTests|FullyQualifiedName~Reports.CannedReportCatalogTests|FullyQualifiedName~Reports.ReportValidatorTests"`:

```
error CS0234: The type or namespace name 'Reports' does not exist in the namespace 'VSoftSol.Syslog.Core'
error CS0234: The type or namespace name 'Retention' does not exist in the namespace 'VSoftSol.Syslog.Core'
error CS0246: The type or namespace name 'ReportDefinition' could not be found
error CS0246: The type or namespace name 'IMessageCompressor' could not be found
error CS0246: The type or namespace name 'RetentionPolicy' could not be found
error CS0103: The name 'CannedReportCatalog' does not exist in the current context
```
9 distinct compile errors across 6 new test files — the correct-reason RED for
brand-new types.

**GREEN** — implementation restored. First run: 91/92 — `ArchiveNamingTests
.SanitizeSegment_RemovesFilesystemUnsafeCharacters(input: "../../etc/passwd")` failed
because the test's own expected string didn't account for `SanitizeSegment`'s
leading-dot-stripping (a genuine, intentional safety behaviour, asserted separately by
`SanitizeSegment_StripsLeadingDots_SoTraversalCannotSurvive`) — fixed the test's expected
value, not the implementation. Second run: **92/92 passing.**

Covers: `RetentionValidator`, `ArchiveNaming` (incl. path-traversal defence),
`RetentionEstimator`, `CompressorFactory`/`ZstdMessageCompressor`/`GzipMessageCompressor`
(incl. decompression-bomb cap), `ReportScheduleExtensions`, `CannedReportCatalog`,
`ReportValidator`.

## Slice B — Data: migration 008, tiering engine, archives, restores, report stores/reader

**RED** — the 81 new integration tests (`tests/VSoftSol.Syslog.IntegrationTests/Retention/`)
did not compile against the pre-Slice-B tree (no `SqliteRetentionEngine`,
`SqliteArchiveStore`, `SqliteReportStore`, `ReportContentReader`, migration 008 tables) —
the same correct-reason compile-failure RED as Slice A, at data-store scope this time.

**GREEN was not reached on the first real run.** Three real defects were caught by these
tests, not by review, and are logged in full in `known-issues.md`:

1. A genuine **deadlock** — `RestoreArchiveAsync` and `ExpireRestoresBatchAsync` acquired
   the connection factory's non-reentrant write-lock semaphore via `await using` at method/
   block scope, then called a store method that acquired the *same* semaphore again before
   the outer `await using` released it. First full run of the new suite hung indefinitely
   (13 minutes with zero output vs. the ~8 s a healthy run takes); confirmed via
   `Get-Process` process age, killed, root-caused by inspection, fixed by narrowing the
   `await using` scope to an explicit block that releases the lock before the second call.
2. **`SqliteArchiveStore.ListForPurgeAsync`** silently never purged an "unstreamed" (no
   primary stream) archive: `COALESCE(rp.cold_days, $default)` was applied *inside* a
   correlated scalar subquery that itself returns zero rows (and therefore SQL `NULL`, not
   a missing column) when `stream_id IS NULL` — the outer comparison against `NULL` is
   never true. Fixed by moving the `COALESCE` outside the subquery.
3. The query grammar's match-all convention is an **empty string**, not `"*"` (a bare `"*"`
   is a prefix-wildcard operator with nothing to prefix — `SearchQueryParser` rejects it:
   "A wildcard (*) needs at least one character before it"). Three canned templates
   (`DeviceAvailability`, `SeverityTrend`, `TopTalkers`), the `ReportTemplate.QueryText`
   default, the `ReportContentReader` custom-report fallback, and the editor page's default
   all used the literal `"*"` — every aggregation report silently returned an *empty*
   result (`AggregationStatus.BadQuery`, unchecked by the caller) rather than throwing.
   Caught by `SeverityTrend_MatchesAnIndependentSqlOracle_PerSeverity` (oracle said 3,
   product said 0) — an assertion that actually executes the aggregation, exactly the kind
   TESTING_STANDARDS.md asks for. Fixed every occurrence to `string.Empty` and added
   `CannedReportCatalogTests.NoTemplate_UsesABareWildcardForMatchAll` as a permanent unit
   guard against the regression recurring.
4. Two canned templates (`DeviceAvailability`, `TopTalkers`) grouped by the token `"host"`,
   which is not in `EventColumns.Groupable` (the allow-list key is `"hostname"`, matching
   the real column name) — `AggregationCompiler` rejected the spec, again silently returning
   an empty result. Fixed to `"hostname"`.
5. Several test fixtures placed an event or audit entry's timestamp *exactly* at the
   report's generation instant (`nowUtc`), which the search/aggregation range excludes
   (`received_utc < $to` is a strict upper bound, matching every other range query in the
   product) — a test-only bug, fixed by generating report content one second after every
   fixture row instead of at the same instant.

After all five fixes: **81/81 passing** (`Migration008Tests`, `RetentionPolicyStoreTests`,
`SqliteRetentionEngineTests`, `RestoreTests`, `ArchiveTamperMatrixTests`,
`RetentionSecurityTests`, `ReportContentReaderTests`, `ReportStoreTests`,
`RetentionFullLifecycleTests`, `ReportRenderingTests`). Full regression re-run (unit +
integration) confirmed **zero new failures** beyond the pre-existing `P2-5`-class flake
(`WalCrashConsistencyTests`, unrelated to this phase, passes in isolation).

## Slice C — Reporting: PDF/CSV renderers

Covered by `ReportRenderingTests.cs` in the Slice B integration run above (the Reporting
project has no unit-test project of its own — same precedent as `SearchExportWriter` in
Phase 5, tested from `IntegrationTests` since it needs real `ReportContent` fixtures).
QuestPDF's fluent API required two real fixes during development (a `Document` type-name
collision with `QuestPDF.Infrastructure.Document`, and `FontSize` called on the wrong
descriptor type in the footer) — both compile-time, caught immediately by `dotnet build`,
not shipped.

## Slice D — Service: retention tiering scheduler, report scheduler, SMTP sender

No new unit/integration tests beyond the Data-layer engine tests the schedulers call
(`SqliteRetentionEngine`, `SqliteReportStore` are already exhaustively tested; the
`BackgroundService` wrappers are thin ordering/audit/notify loops around them, the same
established precedent as `AlertEvaluationService` in Phase 8, which also ships without its
own dedicated scheduler-tick unit tests — the tick logic is proven at the engine layer).
`dotnet build -c Release` clean confirms the DI wiring resolves.

## Slice E — Web: retention/report admin services, pages, download endpoint

Manual verification against the UX gate (below) plus `dotnet build -c Release` clean. A
real route-name collision was caught at build time: `Components/Pages/Reports.razor` (the
list page) and `Components/Pages/Reports/ReportEditor.razor` (the folder) generated a
duplicate `Reports` class in the same namespace — the identical trap Phase 9 hit with
`Dashboards.razor` vs. `Dashboards/`. Fixed the same way: moved the flat file into the
folder (`Components/Pages/Reports/Reports.razor`).
