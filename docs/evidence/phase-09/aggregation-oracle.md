# Phase 9 — aggregation oracle

TESTING_STANDARDS §1 (differential / oracle): every aggregation function is verified against
an **independently written SQL query** over the same fixture. "Sums, counts, and distinct
counts are exactly the kind of thing that looks right and is wrong."

## Method

`tests/…/Dashboards/AggregationOracleTests.cs` seeds `AggregationFixture` (360 deterministic
events over a fixed 6-hour UTC window: 3 hosts, 3 apps, a spread of severities, a third
carrying a numeric `bytes` extracted field), then for each aggregation shape runs
`SqliteAggregationReader.AggregateAsync` and compares its result to a hand-written SQL
string executed directly against the `events` table. The hand SQL shares only the time-range
predicate (`InWindow(e)`); everything else is written independently of the compiler under
test.

## Cases and results

| Aggregation | Oracle SQL | Result |
|---|---|---|
| `Count`, ungrouped | `SELECT COUNT(*)` | exact — 360 |
| `Count`, group by `hostname` | `GROUP BY hostname` | per-group exact |
| `Count`, bucketed hourly | 6 × `COUNT(*)` over each hour | every bucket exact; Σ buckets = 360 |
| `DistinctCount` of `source_ip` | `COUNT(DISTINCT source_ip)` | exact |
| `Sum` of `occurrence_count`, group by `hostname` | `GROUP BY hostname` + `SUM` | per-group exact |
| `Average` of `severity` | `AVG(severity)` | within 1e-9 |
| `Min` / `Max` of `facility` | `MIN` / `MAX` | exact |
| `Sum` of `field.bytes` (extracted) | `JOIN event_fields … name='bytes'` + `SUM(CAST(value AS REAL))` | within 1e-6, > 0 |
| `Count` with a query filter `app:sshd` | `… AND app_name = 'sshd'` | exact, < 360 |
| `Count`, group by `field.bytes` (extracted) | correlated subquery + `GROUP BY 1` | per-group exact |

**0 divergences** across all 10 cases.

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "AggregationOracleTests"
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10
```

The differential is repeated at the Web-service layer in `DashboardSecurityTests`
(cross-scope: two scopes on one shared widget → disjoint totals 30 vs 7, verified against
the seeded per-stream counts).
