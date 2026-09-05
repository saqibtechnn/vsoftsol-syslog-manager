# Phase 1 — Security evidence

SECURITY_STANDARDS.md §8 / PHASE_01 Security Validation.

## Verified

| Gate | How | Result | File / test |
|---|---|---|---|
| SQL injection sweep | CWE-89 corpus into every text field and every query filter | Stored verbatim, matched literally, schema intact; `sqlite_master` unchanged | `SqlInjectionSweepTests` (3), `RepositoryFuzzTests` |
| Parameterisation | `ToFtsPhrase` doubles embedded quotes; every repository statement binds `$`-parameters (no string-built values) | asserted | `SqlInjectionSweepTests.ToFtsPhrase_EscapesEmbeddedQuotes`; analyzer `SCS0002` clean |
| No payload in logs | constraint-failure error path with a secret in the message body; captured logger scanned | zero hits | `RepositorySecurityTests.RepositoryErrorPaths_DoNotWriteMessagePayloadsToTheLog` |
| Fail closed | unresolvable stream / device scope | returns 0 rows, never all | `RepositorySecurityTests.Query_ScopedToAStreamThatHasNoEvents_ReturnsNothing_NotEverything` |
| Hostile-input fuzz | NUL, RTL marks, 1 MB payloads, invalid UTF-8, format specifiers into every method | no exception escapes; raw bytes byte-identical on read-back | `RepositoryFuzzTests` |
| Constraint enforcement | FK / UNIQUE / NOT NULL / CHECK / append-only matrix | every violation rejected | `SchemaConstraintTests` (12) |
| WAL crash consistency | `kill -9` mid-transaction ×20, reopen | `integrity_check = ok`, committed rows intact, DB still writable | `WalCrashConsistencyTests` |
| Audit log append-only in fact | `UPDATE` / `DELETE` on `audit_log` | rejected by `BEFORE` triggers (`RAISE(ABORT)`) | `SchemaConstraintTests` cases |
| SCA | `dotnet list package --vulnerable --include-transitive` | clean — 0 findings, all 12 projects (`Microsoft.Data.Sqlite 8.0.30`, `SQLitePCLRaw` pulled transitively) | `sca-vulnerable.txt` |

## Deferred to a later phase

- **Database / WAL / journal file ACLs** (PHASE_01 "File permissions") — the data
  directory is created by `DatabaseInitializer` at runtime; the ACL that grants only the
  service account is applied by the **installer** (ADR 0006, Phase 12). Asserted on a
  clean VM in the Phase 12 installer security review.
- Second-order / stored-XSS through the log path — the render surfaces (grid, export,
  PDF) are Phases 5/9/10; the store keeps raw bytes and never sanitises on ingest, per
  Constraint 4 and SECURITY_STANDARDS.md §5.2.

## Runs in CI

The Phase 0 `ci.yml` gates (SCA fail-on-any, CodeQL, Gitleaks, arch + literal, repro
build) cover this phase's code unchanged.
