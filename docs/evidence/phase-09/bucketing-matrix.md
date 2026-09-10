# Phase 9 — time-bucketing matrix

TESTING_STANDARDS + the phase's Validation section: "bucket boundaries across DST forward
and back, leap day, year boundary, and a device sending in a different timezone. Assert no
double-counted or dropped buckets."

## Design

Bucket index is **integer `strftime('%s')` seconds from a fixed origin** (the window's lower
bound), never `julianday()` float subtraction (the Phase 8 `sqlite-time-bucketing` note).
`(strftime('%s', received) - strftime('%s', origin)) / bucketSeconds` on the SQL side;
`(instant.ToUnixTimeSeconds() - origin.ToUnixTimeSeconds()) / bucketSeconds` in
`TimeBucketing.BucketPlan.IndexOf`. Because the maths is pure elapsed-seconds from a fixed
origin, a bucket that straddles a DST transition or a midnight is still exactly
`bucketSeconds` of real time — nothing is double-counted or dropped. Bucket **labels** are
converted to local only at render.

## Unit matrix (`TimeBucketingTests`, 18 tests)

| Case | Assertion |
|---|---|
| Auto interval ladder | 15 min → 1 m; 4 h → 5 m; 24 h → 15 m; 7 d → 6 h; 30 d → 6 h; 1 y → 1 w; 10 y → 1 w (cap) |
| `Plan` covers `[from, to)` | ragged windows round bucket count up; clamp at `MaxBuckets` = 500 |
| US DST spring-forward (2026-03-08 07:00Z) | every bucket start is exactly 3600 s after the previous — no 23- or 25-hour day; `IndexOf` exact through the transition |
| US DST fall-back (2026-11-01 06:00Z) | same |
| Leap day (Feb 29 2028) | 48 hourly buckets Feb 28 → Mar 1; noon Feb 29 → bucket 36 |
| Year boundary (2026→2027) | 24 contiguous hourly buckets; 00:30 Jan 1 → bucket 12 |
| Device in +13:00 (Tonga) | `2026-09-01T09:00+13:00` → UTC `2026-08-31T20:00Z` → hour bucket 20, whether passed as the offset value or `.ToUniversalTime()` |
| Pre-origin instant | index −1, `InRange` false |

## Integration matrix (`TimeBucketMatrixTests`, real SQLite `strftime`, 7 tests)

`SqliteAggregationReader` bucketed `COUNT(*)` against real rows:

| Window | Result |
|---|---|
| DST spring-forward day, 20-min events | Σ bucket counts = event count; every bucket index in `[0, plan.Count)` |
| DST fall-back day | same |
| Leap day (Feb 28 → Mar 1) | same |
| Year boundary | same |
| Device sending `+13:00` | single event lands in hour bucket 20; `BucketStartUtc(20)` = `2026-08-31T20:00Z` |
| Event exactly on a bucket boundary | counts once, in the **later** bucket; one tick before → earlier bucket |

**No double-counted or dropped buckets in any case.**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "TimeBucketMatrixTests"
Passed!  - Failed: 0, Passed: 7
dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "TimeBucketingTests"
Passed!  - Failed: 0, Passed: 18
```
