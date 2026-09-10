# Phase 9 — security evidence

Per SECURITY_STANDARDS §8. Phase 9 adds one output surface (dashboard widgets) and one
shared artifact (a shared dashboard viewed by users with different scopes). The threats and
their tests:

## Cross-scope leakage on shared dashboards — the highest risk

**"An aggregate that includes out-of-scope events is a data leak even when no individual
event is displayed."**

Mitigation: every widget aggregation is compiled on top of the Phase 5 `SearchCompiler`
predicate, which bakes the viewer's `UserScope` stream/device clauses into the `WHERE`
(ADR 0017). There is no code path that aggregates events the viewer cannot see.

Tests:
- `AggregationScopeTests` (reader layer): two scopes on the same shared widget →
  `Count` returns 120 vs 45 (the per-stream seeded totals), never 165; a grouped aggregate
  under scope A never names an out-of-scope group; `DistinctCount` counts only in-scope
  values; a bucketed series sums to the in-scope total only; a scope that matches no events
  returns an empty `Ok` result, not an error.
- `DashboardSecurityTests` (Web-service layer): two `WidgetDataService` viewers scoped to
  disjoint streams load one shared bar widget → disjoint group sets and totals (30 vs 7).

## Stored XSS — widget titles, dashboard names, axis / category labels

Hostnames and app names come from the wire and become bar labels, donut legends, table
rows, and the widget frame title.

Mitigation: values are stored **byte-identical** (never sanitised on ingest — Constraint 4)
and encoded **at render** — Blazor's default output encoding for every `@value`, and the
snapshot tests confirm the components emit `@c.Label` / `@r.Message` as encoded text, never
markup. `DashboardSecurityTests.StoredXss_…` stores `<script>alert(1)</script>` in a widget
title and a dashboard name and asserts the persisted value is unchanged.

## IDOR — dashboards and widgets by id

Mitigation: `SqliteDashboardStore.GetAsync` returns a row only if the caller owns it, it is
shared, or it is a system dashboard; `UpdateAsync` / `DeleteAsync` require `owner_user_id =
$uid AND is_system = 0`. No existence oracle — a forbidden id returns null, not 403.

Tests: `DashboardPersistenceTests.Get_AnotherUsersPrivateDashboardById_ReturnsNull`,
`Update_ByANonOwner_DoesNothing`, `Delete_ASystemDashboard_IsRefused`,
`Update_ASystemDashboard_IsRefused`; `DashboardSecurityTests.Idor_…`;
`DashboardWebTests.Save_ASystemDashboard_IsRefused`.

## Cache poisoning

Covered above (cross-scope) — the cache key carries `ScopeFingerprint`. See
`cache-keying.md`.

## Authorization

- Routes: `/dashboards`, `/dashboards/{id}` → `ViewData` (all four roles);
  `/dashboards/{id}/edit`, `/dashboards/new/edit` → `Operate` (Administrator + Operator).
  `DashboardWebTests.Routes_RequireAuthentication` asserts the 302 → `/login`.
- Role at the service: `DashboardService.SaveAsync` / `DeleteAsync` refuse anyone who is
  not Administrator or Operator; `CopyAsync` is available to every authenticated user
  (default dashboards are "copyable"). `DashboardWebTests.Save_IsRefusedForReadOnly`,
  `Copy_IsAvailableToReadOnly`.
- Every mutation is audited (`dashboard.create` / `update` / `delete` / `copy`) with the
  true actor. `DashboardWebTests.Create_ByOperator_IsAllowed_AndAudited`.

## Not applicable

- **CSV / formula injection** — Phase 9 has no export path (that is Phase 10).
- **SSRF / command injection / path traversal** — no outbound actions from a dashboard.

## SAST / SCA / secrets

- `dotnet build -c Release` — 0 warnings (analyzers + SecurityCodeScan clean).
- **No new dependency** — the aggregation cache is hand-rolled; `HtmlRenderer` is the ASP.NET
  shared framework. `dotnet list package --vulnerable --include-transitive` — see
  `sca-vulnerable.txt`.
- Gitleaks — clean (no secrets in the new code or fixtures).

## Findings

**0 Critical / 0 High / 0 Medium / 0 Low.** Carried environmental item: P9-1 (50M-event
dashboard-load acceptance on a clean VM → Phase 12), the P1-1 / P5-1 / P6-1 pattern; not a
security finding.
