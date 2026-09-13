# v1.1 — unify SqliteLogRepository onto EventRowMapper (P5-4 closed) — red/green

Per `TESTING_STANDARDS.md`: every new test observed failing before it passed — but this item
is a pure refactor with **zero intended behavior change**, not a feature or a bug fix, so
there is no new behavior to write a failing test against. The phase-05 known-issue itself
frames the safety net this way: "Both project the same fixed schema and the oracle +
repository tests guard equivalence" — i.e. the existing test suite, unmodified, is the
correctness proof for this change, and its continued green-ness after the refactor **is**
the red/green evidence.

## What changed, mechanically

`SqliteLogRepository` had its own private, byte-for-byte duplicate of everything
`EventRowMapper` (the Phase 5 search-executor's shared row mapper, in the same
`VSoftSol.Syslog.Data.Repositories` namespace) already does:

| Removed from `SqliteLogRepository` | Replaced with |
|---|---|
| `private const string EventColumns` | `EventRowMapper.Columns` |
| `private static string Prefixed(...)` | `EventRowMapper.Prefixed(...)` |
| `private static class EventReader { Read(...) }` | `EventRowMapper.Read(...)` |
| `private static SyslogEvent CloneWithFields(...)` | `EventRowMapper.WithFields(...)` |
| `private static Task<...> LoadFieldsAsync(...)` | `EventRowMapper.LoadFieldsAsync(...)` |

Diffed line-by-line before deleting: both `Read` implementations read the exact same 20
columns at the exact same positional indices (including the Phase 10 `tier`-is-trailing
comment and the `WarmTierCodec.Decode` call), and both `CloneWithFields`/`WithFields`
constructed the identical `SyslogEvent` object shape. `SqliteLogRepository`'s own
general-purpose `BindList` helper (used by `AppendFilter` for severity/device/stream IN-list
clauses unrelated to field-loading) was left untouched — only the field-loading IN-list
inside the now-deleted private `LoadFieldsAsync` was actually redundant with
`EventRowMapper.LoadFieldsAsync`'s own IN-list building.

## The actual safety net

```
$ dotnet build src/VSoftSol.Syslog.Data -c Release
Build succeeded. 0 Warning(s), 0 Error(s).
```
No unused-member/unused-using warnings after deleting ~90 lines of dead-duplicate code —
confirms nothing else in the project referenced the removed private members.

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SqliteLogRepositoryTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 940 ms
```

`AppendAsync_ThenGetById_RoundTripsEveryField` in particular asserts every single
`SyslogEvent` property plus `Fields` round-trips exactly — this is precisely the test that
would fail if `EventRowMapper.Read`/`WithFields` mapped so much as one column differently
than the deleted private code did. It — and the other five cases (unknown id, batch-append
ordering, filter/order/paging, context) — passed unchanged, with zero test-file edits.

Full regression (unit + integration) is in `verification.md`.
