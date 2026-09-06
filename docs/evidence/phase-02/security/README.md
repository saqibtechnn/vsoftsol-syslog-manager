# Phase 2 — Security evidence

SECURITY_STANDARDS.md §8 / PHASE_02 Security Validation. The listener is unauthenticated
and reachable by anyone with network access — treated as an internet-facing service.

## Verified

| Gate | How | Result | Test |
|---|---|---|---|
| **Availability as a security property** — flood at 10× the rate limit from 100 spoofed source IPs | 100 sources × 1000 bursts through `FrameIntake` with `Drop` breach behaviour, plus a legitimate in-budget source mixed in | Service stays up; ~90k frames shed **with a counter** (`DroppedRateLimited`); spill stays under its 16 MB cap; the well-behaved source's 50 frames all committed | `IngestionSecurityTests.Flood_TenTimesTheRateLimit...` |
| **Malformed frame fuzzing** | Random bytes, zero-length, 64 KB, split framing, mixed framing on one connection, NUL runs, invalid PRI — into both listeners (20k fast / **1,000,000** Soak, both green: 1M run received 685,845 frames, listeners kept ingesting) | **Zero crashes, zero hangs, no socket/handle leak** (handle count delta < 2000), no unbounded allocation; a well-formed message still gets through afterwards | `WireFuzzTests.HostileFrames_NeverCrashOrHang...` |
| **Slowloris / connection exhaustion** | 2,000 half-open TCP connections held open sending nothing | Connection cap (`TcpMaxConnections`) holds; idle connections closed after `TcpIdleTimeout`; **UDP ingestion completely unaffected** (2,000 datagrams all committed during the slowloris) | `IngestionSecurityTests.Slowloris_TenThousandHalfOpen...` |
| **Spill disk-full** | Repository stalled + `SpillMaxBytes` cap reached | Graceful degradation: over-cap frames dropped **with a counter** (`DroppedSpillFull`) and an **error-level alert**; `integrity_check = ok`; the ledger balances (`committed + dropped == accepted`); frames already on disk unaffected | `ChaosTests.DiskFullDuringSpill_DegradesGracefully...` (×10 Soak) |
| **Hard-kill durability** | `Process.Kill()` mid-ingest at ~12.5k msg/sec over TCP, restart, recover | Every frame durable before the kill (committed, or fsync'd to spill) is present after recovery; `integrity_check = ok` | `KillRecoveryTests` (×3 fast, ×10 Soak) |
| **Bind-address assertion** | Listener defaults + the Web host's `appsettings.json` | Listeners default to `0.0.0.0` (correct for a LAN syslog collector) and are fully configurable; the **UI host binds `localhost` only** and its config contains no `0.0.0.0` | `IngestionSecurityTests.Listeners_BindOnlyWhereConfigured...` |
| **Rate-limit correctness** | Token bucket per source under a virtual clock | Throttle keeps every frame (counter only); Drop / Quarantine discard **only** while over budget / quarantined, always counted; one abusive source never starves another | `PerSourceRateLimiterTests` (5), `RateLimitTests` (5) |
| **Raw bytes preserved (Constraint 4)** | Oversized frames, embedded NUL / CR / LF, invalid UTF-8 | `raw_message` stored byte-identical; oversized frames stored **truncated to `MaxMessageBytes` with a `truncated` field**, never dropped | `UdpIngestionTests`, `SyslogStreamFramerTests`, `WireFuzzTests` |
| **No accepted message silently lost** | Every load/chaos test asserts `sent == committed + dropped (+ in-flight)` with `dropped == 0` for the zero-loss cases | Ledger balances in every case | `UdpIngestionTests`, `TcpIngestionTests`, `BackpressureSpillTests`, `ChaosTests` |
| **SCA** | `dotnet list package --vulnerable --include-transitive` | Clean — 0 findings, all 13 projects | `sca-vulnerable.txt` |
| **SAST** | Roslyn + SecurityCodeScan analyzers as build errors; `TreatWarningsAsErrors` | Build warning-clean; no new `Process.Start`, no SQL construction, no crypto, no disabled cert validation in the ingestion code | build output |

## Spoofing acknowledgement (accepted residual risk)

Plain UDP syslog source IPs are unverifiable and trivially spoofable. This is recorded as
an **accepted residual risk** in `docs/security/THREAT_MODEL.md` (B1) with the mitigations
offered (per-source rate limiter now; source-subnet allow-list Phase 6; mutually-
authenticated TLS listener Phase 11). Also accepted: the ≤ `SpillFlushInterval` /
in-memory-channel loss window on a hard `kill -9` (ADR 0010). Both are stated in the
Phase 12 hardening guide and recorded in `SECURITY_REVIEW.md` for operator sign-off.

## Deferred to a later phase

- Spill / segment / cursor file ACLs → Phase 12 installer (ADR 0006), same as the DB ACL.
- CRLF-injection `framing_anomaly` field, ReDoS guards on user regex → Phase 3 (parser).
- TLS listener with mutual auth → Phase 11.

## Runs in CI

The Phase 0 `ci.yml` gates (SCA fail-on-any, CodeQL, Gitleaks, arch + literal, repro
build) cover this phase's code unchanged. The wire-fuzz and chaos Soak variants run on the
nightly schedule.
