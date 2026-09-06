# Phase 2 — known issues

| ID | Severity | Issue | Plan |
|---|---|---|---|
| P2-1 | info | `events.listener_id` is stored as NULL. Linking events to a persisted `listeners` row needs the listener-management UI. | Phase 4 (ADR 0010). Per-listener counters already work, keyed by listener name. |
| P2-2 | info | Spill / segment / cursor files inherit the data-directory ACL. The service-account-only ACL is applied by the installer. | Phase 12 installer security review (ADR 0006), alongside the database ACL. |
| P2-3 | info | Hard-kill residual-loss window: frames in the in-memory channel, or in the ≤ `SpillFlushInterval` (100 ms default) not-yet-fsynced tail of the spill segment, are lost on `kill -9`. Inherent to a non-per-message-fsync design and to unacknowledged UDP. | Accepted residual risk (ADR 0010, THREAT_MODEL B1). Documented in the Phase 12 hardening guide; `SpillFlushInterval` is tunable down to 20 ms for deployments that want a smaller window. |
| P2-4 | info | Spill recovery is **at-least-once**: a crash between a database commit and the cursor fsync re-delivers those frames, producing a small number of duplicate rows. | Accepted for v1 (dedup is out of scope, PHASE_02). No downstream component assumes row uniqueness. |
| P2-5 | info | The kill test spawns the `IngestionProbe` out-of-process. Early flakiness (probe hangs / status timeout) was traced to two real bugs and fixed: unread stdout/stderr pipes deadlocking the child, and a `File.Move`-based status write racing the parent's read. The probe now binds an ephemeral port and reports it (no free-port race), drains its pipes, and writes status with a shared-read retry. The 3× fast variant passed 5/5 consecutive runs. The 10× variant is a nightly Soak test. |
| P2-6 | info | RFC 6587 octet-counting auto-detection keys on a leading ASCII digit. A newline-framed message whose first character is a digit (rare — syslog messages start with `<PRI>`) would be misread as octet-counted. | Documented limitation. The `<` of a well-formed syslog PRI disambiguates in practice; TLS/structured intake (Phase 11) can pin the framing per listener. |
| P2-7 | info | The ingest throughput / latency benchmark is a single-box measurement on the VMware dev VM. | Re-run in Phases 3/6/7 (parser, stream routing, rules all sit on the ingest path — TESTING_STANDARDS.md §5) and on clean-VM hardware in Phase 12. |

Carried from earlier phases: P0-2 (Stryker runner — first real target is the Phase 3 parser),
P0-3 (CSP `unsafe-inline` — Phase 4), P1-1 (insert benchmark re-measure — Phase 12).
