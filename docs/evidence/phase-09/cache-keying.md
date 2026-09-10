# Phase 9 — aggregation cache: TTL, invalidation, scope keying

The phase's Validation section: "TTL honoured, invalidated on time-range change." Security
Validation: "assert the aggregation cache is keyed by scope so one user's cached result can
never be served to another."

## Key

```
key = ScopeFingerprint(scope) + "␟" + source + aggregation + fromUtc + toUtc + bucket
```

`AggregationCache.ScopeFingerprint`:
- `unrestricted` — its own literal value.
- restricted — `streams:<sorted ids | *>;groups:<sorted ids | *>`. Sorted, so
  `[2,1]` and `[1,2]` fingerprint identically; `streams:[5]` and `groups:[5]` differ.

## Assertions (`AggregationCacheTests`, 7 tests)

| Behaviour | Result |
|---|---|
| Within the TTL (15 s), a second call does **not** re-invoke the factory | `calls == 1` |
| After the TTL, the factory runs again | returns `v2` |
| Same key, **different scope** (`A={1,2}` vs `B={3}`) → each gets its own value | `A` sees `A-DATA`, `B` sees `B-DATA` — never the other's |
| Same scope, **different time-range key** | recomputes |
| 20 concurrent callers on one hot key | factory runs **once**, all get the same value |
| Factory throws | the failure is **not** cached; next caller retries and succeeds |
| `ScopeFingerprint` | stable for equal scopes, distinct for different, `unrestricted ≠ {1}`, `streams:{5} ≠ groups:{5}` |

## Web-layer confirmation (`DashboardSecurityTests`)

`Cache_IsKeyedByScope_SoOneViewersResultIsNeverServedToAnother`: two `WidgetDataService`
viewers scoped to disjoint streams load the **identical widget**; viewer A primes the cache,
viewer B asks next. A sees 12, B sees 3 — the per-stream seeded counts. A shared wall
dashboard cannot leak one tenant's aggregate to another.

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "AggregationCacheTests"
Passed!  - Failed: 0, Passed: 7
```
