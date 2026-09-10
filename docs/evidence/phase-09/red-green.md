# Phase 9 — red / green log

Every new test was observed failing for the correct reason before the implementation
existed (TESTING_STANDARDS §2.1). Slices are checkpointable; the operator may interrupt at
any boundary.

---

## Slice A — Core: models, time bucketing, validators, chart geometry

**Scope:** `Core/Dashboards/` — `AggregationFunction`, `VisualizationType`, `BucketInterval`,
`SystemMetric`, `AggregationSpec`, `WidgetSource`, `WidgetDefinition`, `WidgetLayout`,
`DashboardDefinition`, `AggregationResult`, `TimeBucketing` (+ `BucketPlan`),
`DashboardWidgetCatalog`, `WidgetValidator` (+ `ValidationResult`), `DashboardValidator`,
`ChartGeometry`. Pure — no I/O.

**RED** — the logic entrypoints (`TimeBucketing.Plan` / `AutoInterval`, `BucketPlan.IndexOf`,
`WidgetValidator.Validate`, `DashboardValidator.Validate`, `DashboardWidgetCatalog.IsGroupable`
/ `IsNumeric`, `ChartGeometry.Decimate` / `NiceCeiling` / `Polyline` / `AreaPath` / `Donut` /
`GaugeAngle`) each guarded with
`if (DateTime.UtcNow.Year > 2000) throw new NotImplementedException("phase-09 slice A RED: <entrypoint>")`:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Dashboards"
Failed!  - Failed:    78, Passed:     5, Skipped:     0, Total:    83

  every logic test → System.NotImplementedException : phase-09 slice A RED: <entrypoint>
    TimeBucketingTests            (18) — TimeBucketing.Plan / AutoInterval / BucketPlan.IndexOf
    WidgetValidatorTests          (18) — WidgetValidator.Validate
    DashboardValidatorTests        (7) — DashboardValidator.Validate
    ChartGeometryTests            (21) — ChartGeometry.*
    DashboardWidgetCatalogTests   (14) — DashboardWidgetCatalog.IsGroupable / IsNumeric
  (the 5 passing assert pure data tables — RuleFor, SystemSeriesVisualizations,
   GroupableFields membership, BucketPlan.InRange / BucketStartUtc — which have no guard)
```

**GREEN** — guards removed, implementations restored:

```
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~Dashboards"
Passed!  - Failed:     0, Passed:    83, Skipped:     0, Total:    83
```

## Slice B — Data: migration 007, dashboard store, aggregation compiler + reader, cache, system series, default dashboards

**Scope:** `Data/Migrations/Scripts/007_dashboards.sql`, `Data/Dashboards/` (`EventColumns`,
`DashboardJson`, `SqliteDashboardStore`, `AggregationCompiler` + `CompiledAggregation`,
`SqliteAggregationReader`, `SystemSeriesReader`, `AggregationCache`),
`Seed/DefaultDashboards` + the `DatabaseSeeder` upsert, `AuditActions` (+4 dashboard verbs),
`SqliteSavedSearchStore.GetQueryTextAsync`, DI registration.

**RED** — migration 007 held back (`.sql.hold`, clean Data rebuild) and the logic
entrypoints (`SqliteAggregationReader.AggregateAsync`, `SystemSeriesReader.LatestAsync` /
`RangeAsync`, `SqliteDashboardStore.CreateAsync` / `GetAsync`, `AggregationCache.GetOrAddAsync`)
guarded with `if (DateTime.UtcNow.Year > 2000) throw new NotImplementedException("phase-09 slice B RED: …")`:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Dashboards"
Failed!  - Failed:    46, Passed:     4, Skipped:     0, Total:    50

  Migration007Tests           — no such table: dashboards / collector_stat_samples
  DashboardPersistenceTests   — no such table: dashboards
  AggregationOracleTests      — NotImplementedException phase-09 slice B RED: SqliteAggregationReader.AggregateAsync
  AggregationScopeTests       — "  "
  TimeBucketMatrixTests       — "  "
  DefaultDashboardsTests      — "  " (the pure-Core validation tests stay green: 4 pass)
  AggregationCacheTests       — NotImplementedException phase-09 slice B RED: AggregationCache.GetOrAddAsync
  SystemSeriesReaderTests     — NotImplementedException phase-09 slice B RED: SystemSeriesReader.*
```

**GREEN** — migration restored, guards removed:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Dashboards"
Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50

  AggregationOracleTests — every function × grouped/flat × bucketed/flat matches independent SQL, 0 divergences
  TimeBucketMatrixTests  — DST±, leap day, year boundary, +13 tz: Σ buckets == flat count, every case
  AggregationScopeTests  — two scopes on one shared widget see disjoint totals; no out-of-scope group ever named
```

GREEN-phase code fix (kept): the `dashboards.system_key` unique index was **partial**
(`WHERE system_key IS NOT NULL`), which SQLite refuses as an `ON CONFLICT(system_key)`
upsert target ("does not match any PRIMARY KEY or UNIQUE constraint"). Changed to a plain
`UNIQUE` column — SQLite treats multiple NULLs as distinct, so user rows (NULL key) are
unaffected, and the seeder's idempotent upsert now works. GREEN-phase test fix:
`AggregationScopeTests` — a non-empty scope that happens to match no events returns an empty
`Ok` result (the phase's "a scoped user sees their data, not an error"), not a proven-empty
status; the assertion was corrected to match.

---

## Slice C — Service: collector-stat sampler + options + wiring

**Scope:** `Service/Hosting/DashboardOptions.cs` (+ `CollectorStatOptions`),
`Service/Hosting/CollectorStatSampler.cs`, wiring in `SyslogPlatformExtensions`
(`AggregationCache` TTL bound to the `Dashboards` section in `AddSyslogPlatform`;
`AddHostedService<CollectorStatSampler>()` in `AddCollectorRuntime`).

**RED** — `CollectorStatSampler.SampleAsync` guarded:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~CollectorStatSampler"
Failed!  - Failed: 3, Passed: 0   (all: NotImplementedException phase-09 slice C RED: CollectorStatSampler.SampleAsync)
```

**GREEN** — guard removed:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~CollectorStatSampler"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

No GREEN-phase fix needed in slice C.

---

## Slice D — Web services: DashboardService, WidgetDataService, picker model

**Scope:** `Web/Dashboards/DashboardService.cs` (CRUD + copy + save-layout, role at the
service, audited), `Web/Dashboards/WidgetDataService.cs` (widget → scoped `WidgetData`,
cache-served), `Web/Dashboards/WidgetPickerModel.cs`, `WebSecurityExtensions` registration.

**RED** — `DashboardService.SaveAsync` / `CopyAsync` and `WidgetDataService.LoadAsync` guarded:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "DashboardWebTests|DashboardSecurityTests"
Failed!  - Failed: 11, Passed: 4, Skipped: 0, Total: 15
  (the 4 passing are route-auth theory cases, List, and the pure WidgetPickerModel.Build test)
  every mutating / loading test → NotImplementedException phase-09 slice D RED: <entrypoint>
```

**GREEN** — guards removed:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "DashboardWebTests|DashboardSecurityTests"
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15

  DashboardSecurityTests — cross-scope: two viewers of one shared widget get disjoint totals
                           (30 vs 7), neither names the other's host; cache is scope-keyed;
                           hostile widget titles / dashboard names stored verbatim; IDOR on
                           another user's private dashboard id returns null
```

No GREEN-phase fix needed in slice D.

## Slice E — Web UI: pages, widget components, guided picker, drag grid, CSS

**Scope:** `Web/Components/Pages/Dashboards/` (`Dashboards`, `DashboardView`,
`DashboardEditor`, `WidgetPicker` + `Widgets/` — `WidgetCard` and the 8 pure viz
components), `Web/Dashboards/WidgetChartModel.cs`, `wwwroot/ds.css` Phase 9 block; the old
`Components/Pages/Dashboards.razor` `ComingSoon` deleted (route conflict).

**RED** — the components are exercised by `WidgetComponentTests` via
`Microsoft.AspNetCore.Components.Web.HtmlRenderer` (bunit is banned — the
dev-vm-constraints note). Visual-regression snapshots did not exist:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "WidgetComponentTests"
Failed!  - Failed: 7, Passed: 10, Skipped: 0, Total: 17

  Widget_RendersPixelStable(*) → "Snapshot '<name>' did not exist and was written. Re-run to verify, then commit it."
  (the 10 passing: every widget renders an empty state without throwing; 100k-point series;
   all-identical and negative values)
```

**GREEN** — the 7 `tests/fixtures/dashboards/*.snapshot.html` committed, re-run:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "WidgetComponentTests"
Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17
```

GREEN-phase fixes (kept): (a) `RateGauge` needle coordinates are rounded to 1 dp so the SVG
output is deterministic; (b) the snapshot `Normalize` redacts the time-axis label text — it
renders bucket starts in the machine's local timezone (correct for a user, not
deterministic across machines, TESTING_STANDARDS §2.3); the polyline geometry, which is what
visual regression protects, stays byte-checked; (c) `PathMap` (`ContinuousIntegrationBuild`)
makes `[CallerFilePath]` return `/_/…`, so the snapshot directory is resolved by walking up
from the test assembly to the folder holding `VSoftSol.Syslog.sln`.

---

GREEN-phase fix (test-side only): `ChartGeometryTests.Polyline_FormatsIndependentOfCurrentCulture`
was rewritten to `Polyline_UsesAnInvariantDecimalPoint` — the build VM runs in
`InvariantGlobalization=true` mode (see the `dev-vm-constraints` note), so
`new CultureInfo("de-DE")` throws `CultureNotFoundException`. The assertion (coordinates use
`.` not a locale comma) is unchanged; only the mechanism (asserting the format string
directly rather than switching `CurrentCulture`) changed. No production code changed in the
GREEN phase.
