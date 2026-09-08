# Phase 6 — verification (phase prompt "Verification" section)

## `dotnet test --filter "Stream|Device|Discovery"`

```
Condition engine    ConditionEvaluatorTests, ConditionCompilerTests,
                    ConditionCompilerReDoSTests (Rules) ...................... PASS
Routing oracle      StreamRoutingOracleTests — 10,000 generated messages ×
                    50 generated stream definitions, 0 divergences .......... PASS
Migration 004       Migration004Tests — devices columns, device_ips (unique
                    ip), discovery_settings seeded, streams is_catch_all .... PASS
Discovery           DeviceDiscoveryTests — 5,000 msgs / one source → 1 pending;
                    20 concurrent sources → 20 devices, 0 dups; spoof flood
                    bounded at MaxPendingDevices + drop count; resolver 1
                    DB write / 100k resolutions; queue-full pauses discovery . PASS
Routing (ingest)    StreamRoutingIntegrationTests — 7 defaults compile;
                    representative messages route as expected; raw → Parse
                    Failures + All Messages; event_streams written at ingest
                    in the insert transaction; bad-regex stream dropped &
                    reported, others keep routing; router rebuilds on Version   PASS
End-to-end          IngestRoutingEndToEndTests — frame → parse → discover
                    device → route → commit; enricher throw never loses a msg   PASS
Web surface         DeviceWebTests — /devices, /devices/pending, /streams,
                    /streams/tester require auth; pending queue is Admin-only;
                    approve/reject refused for Operator + Read-Only AT THE
                    SERVICE; hostile hostname renders encoded ............... PASS
Scope + XSS         StreamScopeAndXssTests — stream-scoped ListAsync/GetAsync/
                    SaveAsync (no IDOR, no existence oracle); hostile
                    Name/Hostname/Vendor encoded on the health card ........ PASS
```

Full runs: `test-output-integration.txt` — **integration 336 passed, 0 failed, 0 skipped**;
unit **632 passed, 0 failed, 0 skipped**. The load-dependent P2-5 WAL flake
(`WalCrashConsistencyTests.HardKill…TwentyTimes`) passes in isolation and did not recur on
the recorded Release run — see `known-issues.md`.

## `dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --ingest-probe --streams 20`

See `benchmarks.md` / `benchmarks.json` / `benchmark-run.txt`. Summary:

| parse mode | streams | msg/sec | vs baseline |
|---|---|---|---|
| RFC + vendor extraction | 0  | 3,535 | — |
| RFC + vendor extraction | 20 | 3,193 | −9.7 % |
| RFC header only          | 20 | **6,706** | gate PASS |

Stream routing costs **~10–12 %** of ingest throughput (20 compiled condition trees
evaluated per message + `event_streams` links). The 5,000 msg/sec gate is met with 20
streams active on the RFC path; the vendor-extraction path is below the gate at baseline on
this 2-vCPU VMware VM as well (pre-existing **P3-2**), and Phase 6 adds ~10 % on top rather
than causing the shortfall. The literal "≥ 5,000 with vendor extraction **and** 20 streams"
confirmation is carried to the Phase 12 clean-VM acceptance run — the same carry agreed for
P1-1 and P3-2. Post-stream-routing throughput is recorded in `PROGRESS.md` per the phase
Definition of Done.

## Tests-to-write-first (phase prompt list)

| Required test | Where | Result |
|---|---|---|
| Message matching three streams present in exactly those three | `StreamRoutingOracleTests`, `StreamRoutingIntegrationTests` | ✓ |
| Message matching none lands only in All Messages | `StreamRoutingOracleTests.NonMatch…` | ✓ |
| Catastrophic-backtracking pattern rejected or timeboxed | `ConditionCompilerReDoSTests` | ✓ |
| Unknown source → one pending record, not one per message | `DeviceDiscoveryTests` (5,000 msgs → 1) | ✓ |
| Stream-scoped users see only their streams in every list/picker | `StreamScopeAndXssTests`, `StreamAdminService` (backs every picker) | ✓ |

## Live host (`dotnet run -c Release --project src/VSoftSol.Syslog.Web`, Production)

New routes, all behind auth (verified by `DeviceWebTests.Routes_RequireAuthentication`):

```
GET /devices           -> 302  /login?returnUrl=%2Fdevices
GET /devices/pending    -> 302  /login   (then 403/redirect for non-Administrators)
GET /devices/groups     -> 302  /login
GET /streams            -> 302  /login
GET /streams/tester     -> 302  /login
GET /settings/discovery  -> 302  /login
```

The nav pending-device badge (`NavMenu.razor` → `SqliteDeviceStore.CountPendingAsync`) and
the health card render are exercised in the pre-rendered HTML by the web tests above.
