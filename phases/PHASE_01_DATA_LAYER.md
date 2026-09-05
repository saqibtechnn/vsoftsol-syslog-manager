# PHASE 1 — Data Layer

## Context
Phase 0 gave you a compiling skeleton with `ILogRepository` defined but unimplemented.
This phase makes the database real. Read `CLAUDE.md`, `PROGRESS.md`, and the canonical
event schema in `BUILD_PLAN.md`.

## Objective
A migrated SQLite database in WAL mode with a working repository implementation and a
recorded insert benchmark.

## Build
1. SQLite connection factory with WAL enabled, `synchronous=NORMAL`, busy timeout, and
   a single-writer discipline documented in code.
2. Forward-only migration runner (plain SQL files, versioned, checksummed, idempotent).
   Migration 001 creates: `events`, `event_fields`, `devices`, `device_groups`,
   `listeners`, `streams`, `rules`, `users`, `roles`, `audit_log`, `schema_version`.
3. FTS5 virtual table over `events.message` and `events.raw_message`, kept in sync by
   triggers. Verify tokenizer handles IP addresses and MAC addresses sensibly.
4. Indexes on `received_utc`, `source_ip`, `severity`, `device_id`, and
   `event_fields(name, value)`.
5. `SqliteLogRepository` implementing `ILogRepository`, with **batched inserts**
   (transaction per batch, configurable batch size, default 500) — single-row inserts
   will not meet the ingest target.
6. Retention-aware deletes that do not lock the writer for more than 100 ms per pass.
7. Seed data: default roles, a default admin user, the seven default streams named in
   the feature spec.

## Do not build in this phase
Listeners, parsers, UI, or any query language. Repository methods used later may exist
as signatures but must be tested if implemented.

## Tests to write first
- Round-trip persistence test for every entity.
- Migration test: empty file → migrated → re-run migration is a no-op.
- FTS5 test: insert 10 messages, search for a substring, assert correct hits.
- Concurrency test: one writer + five readers, no `SQLITE_BUSY` failures.
- Retention delete test asserting the 100 ms bound.

## Verification — run these and paste output
```bash
dotnet test tests/VSoftSol.Syslog.UnitTests
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Insert*"
```
Record the 1,000,000-row batched insert throughput in PROGRESS.md. If it is below
20,000 rows/sec, fix it now — Phase 2 cannot make up the difference.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Migration validation** — empty → migrated → re-run is a no-op; migrating a database
  seeded with 100k rows preserves every row (count and checksum before/after).
- **Constraint enforcement** — a test per foreign key, unique index, NOT NULL, and check
  constraint, asserting the violation is rejected rather than silently accepted.
- **WAL crash consistency** — `kill -9` mid-transaction, reopen, assert the database is
  not corrupt and the last committed transaction is intact. Repeat 20 times.
- **Concurrency** — 1 writer + 5 readers for 60 s, zero `SQLITE_BUSY` failures,
  zero torn reads.
- **Property-based** — round-trip any generated event through write/read and assert
  field-for-field equality, including nulls, unicode, and 64KB messages.
- **Fuzz** — hostile strings (null bytes, RTL marks, 1MB payloads, SQL fragments) into
  every repository method; assert no exception escapes and no injection occurs.
- **Benchmark discipline** — the 1M-row insert benchmark runs **three times**; report
  mean and standard deviation. A variance over 15% means an unreliable measurement,
  not a passing gate.
- **Evidence:** `benchmarks.json`, crash-consistency run log, constraint matrix results.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **SQL injection sweep** — every repository method receives the CWE-89 corpus plus
  unicode-normalization and second-order payloads. Assert parameterization by inspecting
  the generated command text, not just the result.
- **File permissions** — the database, WAL, journal, and backup files are readable only
  by the service account. Assert the ACL programmatically.
- **No sensitive data in internal logs** — trigger every repository error path with
  message content present; grep Serilog output for payload text. Zero hits.
- **Fail-closed test** — a query whose scope filter cannot resolve returns zero rows,
  never all rows. This one default has caused more real breaches than any clever attack.
- **Evidence:** injection sweep results, ACL assertion output, log-leakage scan.

## Definition of Done
Standard DoD, plus the insert benchmark number is committed to PROGRESS.md.

## Commit
`feat: phase 1 — sqlite schema, migrations, fts5, repository` → tag `v1.0.0-phase.1`
