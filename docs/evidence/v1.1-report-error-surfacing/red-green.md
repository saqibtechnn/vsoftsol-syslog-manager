# v1.1 — report query failures surfaced instead of "no data" (P10-2 closed) — red/green

Per `TESTING_STANDARDS.md`: every new test observed failing before it passed.

## 1. `ReportContentReaderTests` (integration, existing file extended)

Two new cases added first:
- `CustomReport_WithAMalformedQuery_SurfacesTheParserErrorInstead_OfLookingLikeNoData`
- `CannedAggregateReport_ScopeExcludesEveryStream_SurfacesWhyInsteadOfLookingLikeNoData`
  (later reworked — see note below)

RED — `ReportContent.Error` existed (added first, defaults to null) but
`ReportContentReader.ResolveAsync` never set it:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportContentReaderTests"
Failed!  - Failed: 2, Passed: 17, Skipped: 0, Total: 19, Duration: 3 s
  CustomReport_WithAMalformedQuery...: Expected string not to be <null> or empty, but found <null>.
  CannedAggregateReport_ScopeExcludesEveryStream...: Expected string not to be <null> or empty, but found <null>.
```

**Correction during GREEN**: the `ScopeExcludesEverything` case could not actually be
produced through `ReportContentReader`'s public surface. `UserScope.Create`/`FromUser` both
treat an *empty* visible-set as "all" (`Create`: `streams.Count == 0` implies
`AllStreams = true`) — the only way `SearchCompiler.AppendScopeClauses` returns
`impossible = true` (which is what `AggregationCompiler` maps to `ScopeExcludesEverything`)
is `AllStreams == false && StreamIds.Count == 0`, which `UserScope`'s `private` constructor
makes unreachable from outside the class. A scope restricted to a *non-empty* set of
non-matching stream ids (what the test originally built) legitimately produces
`AggregationStatus.Ok` with zero rows — a real empty result, not a failure. The test was
reworked into `CannedAggregateReport_ScopeRestrictedButStillMatchesStreams_HasNoError`, a
regression guard proving that boundary (`Error` stays null for a legitimate empty result).
The actual exhaustive `AggregationStatus -> Error` mapping is proven separately (§2) since it
cannot be exercised end-to-end today with a real, reachable `ScopeExcludesEverything` case.

GREEN — after `ReportContentReader.ResolveAsync` populated `Error` on both branches:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportContentReaderTests"
Passed!  - Failed: 0, Passed: 19, Skipped: 0, Total: 19, Duration: 2 s
```

## 2. `ReportContentReaderErrorMappingTests` (new unit-test file)

Written after the implementation (`DescribeAggregationFailure` visibility relaxed from
`private` to `internal`, the only change needed to make it directly testable — no new
architectural seam, `Data` already grants `InternalsVisibleTo` to both test projects). Not a
RED/GREEN pair in the strict sense, flagged honestly rather than presented as one: this
proves the exhaustive `AggregationStatus -> Error` mapping (`Ok`, `BadQuery`,
`BadAggregation`, `ScopeExcludesEverything`, and the null-detail fallback) in isolation,
covering the one status (`ScopeExcludesEverything`) that §1 established cannot currently be
reached end-to-end through the public API.

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~ReportContentReaderErrorMappingTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 10 ms
```

## 3. `ReportRenderingTests` (integration, existing file extended)

RED — `ReportPdfRenderer`/`ReportCsvWriter` did not look at `content.Error` at all:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportRenderingTests"
Failed!  - Failed: 2, Passed: 13, Skipped: 0, Total: 15, Duration: 1 s
  ReportPdfRenderer_Render_WithAnError_...: the errored PDF's bytes were byte-identical to
  the plain-empty-report PDF (the model field was set but nothing read it).
  ReportCsvWriter_WriteAsync_WithAnError_...: expected "# error,unexpected token at 4" in
  the output; the CSV instead fell through to the audit-shaped empty-table branch
  ("occurred_utc,actor,action,entity_type,entity_id,detail" header, zero rows).
```

GREEN — after `ComposeBody` (PDF) short-circuits on `content.Error`, and `WriteAsync` (CSV)
writes a `# error,...` meta line and returns before any row-table branch:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportRenderingTests"
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 1 s
```
