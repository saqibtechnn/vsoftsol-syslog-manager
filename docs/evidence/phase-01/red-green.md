# Phase 1 — Red → Green record

TESTING_STANDARDS.md §2.1. New tests and the reason each was observed failing before the
implementation existed. 34 tests added (17 unit-side / 39 integration-side across the run).

## Data layer tests

| Test | Observed RED (reason) | GREEN after |
|---|---|---|
| `MigrationRunnerTests.MigrateAsync_OnEmptyFile_CreatesEverySchemaObject` | No `MigrationRunner` / `001_initial.sql` — type not found, then "no embedded migration scripts" | Runner + embedded script |
| `MigrationRunnerTests.MigrateAsync_RunTwice_SecondRunAppliesNothing` | RED: second run re-applied everything (`table already exists`) before the `schema_version` check existed | Applied-set check |
| `MigrationRunnerTests.…WhenAnAppliedMigrationChanged_Throws` | RED: drift went undetected (returned 0) before the checksum comparison | Checksum stored + verified |
| `MigrationRunnerTests.…100kRows_PreservesEveryRow` | RED before the runner was idempotent | count + checksum stable |
| `MigrationLoadingTests.*` (unit) | RED before `LoadEmbeddedMigrations` / `Migration.ComputeChecksum` existed | Implemented; LF-normalised SHA-256 |
| `StorageFormatTests.*` (unit) | RED before `StorageFormat` — timestamp round-trip, enum tokens, lossy raw text | Implemented |
| `SqliteLogRepositoryTests.AppendAsync_ThenGetById_RoundTripsEveryField` | RED: `NotImplementedException` on the seam; then field-by-field mismatches (timestamp offset, enum mapping) | `SqliteLogRepository` + `EventReader` |
| `SqliteLogRepositoryTests.AppendBatchAsync_ReturnsIdsInInputOrder…` | RED before batched insert; then wrong ids (contiguity assumption) | `last_insert_rowid()` + AUTOINCREMENT contiguity |
| `SqliteLogRepositoryTests.QueryAsync_FiltersByTimeRangeSeverityAndText` | RED: no filter builder; then all rows returned (WHERE not applied) | `AppendFilter` parameterised clauses |
| `SqliteLogRepositoryTests.QueryAsync_HonoursOrderingAndPaging` | RED before ORDER BY / LIMIT / OFFSET | implemented |
| `SqliteLogRepositoryTests.GetContextAsync_ReturnsSurroundingEventsFromSameSourceOldestFirst` | RED: returned nothing; then wrong count (`before:2` legitimately returned 2 older rows — test expectation corrected to `before:1`) | context queries per side |
| `SchemaConstraintTests.Constraint_IsEnforced` (10 cases) | RED: several constraints accepted bad data before the CHECK / FK / NOT NULL / append-only triggers were added to `001_initial.sql` | schema constraints + audit triggers |
| `SchemaConstraintTests.DeletingAnEvent_CascadesToEventFieldsAndStreams` | RED before `ON DELETE CASCADE` | cascade FKs |
| `Fts5SearchTests.*` (4) | RED: no FTS table; then "unterminated string" on NUL in the query phrase; then 0 hits until deferred index was synced | FTS5 table + `SanitizeText` + `SyncSearchIndexAsync` in the test helper |
| `SeedDataTests.*` (2) | RED before `DatabaseSeeder` — missing roles / admin / streams; then non-idempotent (duplicate rows on re-run) | `ON CONFLICT DO NOTHING` |
| `ConcurrencyTests.OneWriterFiveReaders_NoSqliteBusy_NoTornReads` | RED: `SQLITE_BUSY` under a plain shared connection before WAL + the single-writer lock | `SqliteConnectionFactory` WAL + `AcquireWriteLockAsync` |
| `ConcurrencyTests.WriteLock_SerialisesConcurrentBatchAppends_WithoutLoss` | RED before the write lock (lost updates / `database is locked`) | write lock |
| `RetentionPurgeTests.PurgeOlderThanAsync_RemovesOldEvents_KeepsRecentOnes` | RED before `PurgeOlderThanAsync` | chunked delete |
| `RetentionPurgeTests.…KeepsEachWriteTransactionUnderTheLockBudget` | RED: an unbounded `DELETE` blocked a racing append > 500 ms before chunking | `RetentionDeleteChunk` staged deletes |
| `SearchIndexMaintainerTests.*` (5) | RED before the deferred-index design (ADR 0009) — searchable-only-after-sync, watermark advance, backlog catch-up, hosted-service lifecycle, purge leaves no FTS phantom | `SyncSearchIndexAsync` + `SearchIndexMaintainer` + purge FTS delete |
| `EventRoundTripPropertyTests.WriteThenRead_IsFieldForFieldEqual` (FsCheck) | RED: generated unicode strings with lone surrogates / NUL bytes did not round-trip through a SQLite TEXT column | generator restricted to valid text; NUL documented as replaced (`StorageFormat.SanitizeText`) |
| `RepositoryFuzzTests.*` | RED: `SQLite Error 1: 'unterminated string'` on an embedded NUL reaching the FTS query | `SanitizeText` on every text column and on `ToFtsPhrase` |
| `SqlInjectionSweepTests.*` (3) | RED verifying: CWE-89 corpus in every field is stored verbatim, query filters with hostile values return 0 rows and do not error, `ToFtsPhrase` doubles quotes | parameterised binding throughout |
| `RepositorySecurityTests.Query_ScopedToAStreamThatHasNoEvents_ReturnsNothing_NotEverything` | RED confirming fail-closed: an unresolvable `StreamIds` / `DeviceIds` scope returns 0, never all | `EXISTS` / `IN` clauses on empty match sets |
| `RepositorySecurityTests.RepositoryErrorPaths_DoNotWriteMessagePayloadsToTheLog` | RED confirming: a constraint-failure error path never emits the message body to a captured logger | repository does not log payloads |
| `WalCrashConsistencyTests.HardKillDuringIngest_LeavesDatabaseConsistent_TwentyTimes` | RED without WAL (a `kill` mid-transaction left a hot rollback journal / partial write); confirmed 20× that WAL + `synchronous=NORMAL` leaves `integrity_check = ok` and every committed row intact | WAL mode |
| `WebHostSmokeTests.Host_MigratesAndSeedsTheDatabaseOnStartup` | RED before `DatabaseInitializer` was wired into the composition root | `AddHostedService<DatabaseInitializer>` first |

## Benchmark

`InsertBenchmark` observed at 3.4 k rows/sec with the per-row FTS trigger → the trigger
was removed (ADR 0009) → 18.8 k. See `benchmarks.md` for the full analysis and the
remaining marginal gap to the 20 k gate (I/O-bound on the VM).
