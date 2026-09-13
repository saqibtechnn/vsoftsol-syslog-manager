# v1.1 — unify SqliteLogRepository onto EventRowMapper (P5-4 closed) — verification

Real, observed output from this environment, per TESTING_STANDARDS.md / this project's
Definition of Done. Not a numbered phase (`START_HERE.md` has no Phase 13), held to the
same evidence bar. This is a pure refactor (zero intended behavior change) — see
`red-green.md` for why its safety net is the existing test suite's continued green-ness
rather than a new failing-then-passing test.

## Build — warning-clean

```
$ dotnet build src/VSoftSol.Syslog.Data -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet build -c Release   (full solution)
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

No unused-member/unused-using warnings after deleting the private `EventColumns`,
`Prefixed`, `EventReader`, `CloneWithFields`, and `LoadFieldsAsync` — confirms nothing else
in the solution referenced them.

## Style gate

```
$ dotnet format --verify-no-changes
(no output — exit 0, clean)
```

## Targeted regression check

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SqliteLogRepositoryTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 940 ms
```

All 6 pre-existing cases passed with zero test-file edits — this is the correctness proof
for the refactor (see `red-green.md`).

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1072, Skipped: 0, Total: 1072, Duration: 1 m 8 s
```

Unchanged from the previous v1.1 item — no new unit tests, as expected for a
same-behavior refactor.

## Integration tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 804, Skipped: 0, Total: 804, Duration: 7 m 19 s
```

Same 804 total as the previous v1.1 item (P5-4 added no new tests) — fully clean this run,
neither of the two previously-observed load-sensitive dev-VM flakes reproduced.

## Security

No change in behavior, no change in attack surface. The exact same parameterized SQL and the
exact same scope enforcement (`ScopedEventReader` already used `EventRowMapper`; now
`SqliteLogRepository` does too) run from one shared place instead of two independently
-maintained copies — if anything this *reduces* future risk, since a schema change (a new
column, a renamed one) now only needs updating in one mapper instead of two, closing the
class of bug where the two copies could silently drift apart.

## UI verification

Not applicable — no UI, no route, no user-visible behavior touched. `Data`-layer-only.

## Documentation updated

- `PROGRESS.md` — new v1.1 log entry; "Deferred items across all phases" table's P5-4 row
  struck through with **DONE (v1.1)**; "Current state" bullet updated to list all seven
  closed v1.1 items.
- No `docs/RELEASE_NOTES.md` bullet — nothing a user would observe changed.
- No security-doc section — no query, schema, or scope logic changed, just where it lives.
- `docs/evidence/phase-05/known-issues.md` — left untouched, per this project's "never
  rewrite history" convention (already confirmed via `git log` for prior v1.1 items).

## Conclusion

P5-4 (`docs/evidence/phase-05/known-issues.md`) is closed: `SqliteLogRepository` and the
Phase 5 search executor now share one row-mapping implementation (`EventRowMapper`) instead
of two independently-maintained, byte-for-byte-identical copies. Zero regressions: unit
1072/1072, integration 804/804, both fully clean on this run.
