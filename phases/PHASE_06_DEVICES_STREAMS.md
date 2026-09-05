# PHASE 6 — Devices & Streams

## Context
Search works. Events are still an undifferentiated pile. This phase adds the two
organising concepts everything after it depends on: the device registry and streams.

## Objective
A device registry with auto-discovery, device groups, and stream routing evaluated at
ingest.

## Build
1. **Device registry**: hostname, IP addresses (multiple), vendor, model, role, site,
   group membership, owner, notes, expected message rate, heartbeat threshold, enabled flag.
2. **Auto-discovery**: an unrecognised source IP creates a pending-device record.
   Pending devices appear in an approval queue where an admin names, classifies, and
   approves or rejects them. Unknown-source policy configurable:
   accept-and-auto-register / accept-as-unknown / reject.
3. Events are linked to `device_id` at ingest when the source resolves.
4. **Device groups** — used for RBAC scoping, filtering, dashboards, and bulk rule
   application. A device may belong to multiple groups.
5. **Device health card**: last seen, messages/min trend sparkline, parse-failure rate,
   active alerts, top message types.
6. **Streams**: named routing buckets defined by match rules over any normalized or
   extracted field (equals, contains, regex, numeric range, in-list), combinable with
   AND/OR groups. A message may match multiple streams. Evaluation happens once at
   ingest and is stored, not recomputed at query time.
7. Ship the seven default streams: All Messages, Security Events, Interface Up/Down,
   Authentication Failures, Configuration Changes, Hardware/Environment, Parse Failures.
8. **Stream rule tester**: paste or pick a message, see which streams it would match and why.

## Do not build in this phase
Rules and actions (Phase 7), alerting (Phase 8). Stream *routing* only, no side effects.

## Tests to write first
- Routing test: a message matching three streams is present in exactly those three.
- Non-match test: a message matching none lands only in All Messages.
- Regex-rule safety: assert a catastrophic-backtracking pattern is rejected or timeboxed.
- Discovery test: unknown source creates exactly one pending record, not one per message.
- Scope test: stream-scoped users see only their streams in every list and picker.

## Verification — run these and paste output
```bash
dotnet test --filter "Stream|Device|Discovery"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Ingest*"
```
Stream evaluation runs in the ingest path — re-run the ingest benchmark and confirm
throughput is still ≥ 5,000 msg/sec with 20 active streams configured.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task: **approve a newly discovered device, name it, assign
it to a group, and set its heartbeat threshold.** Build the stream condition editor from
the Phase 4 visual condition builder — a raw expression box is available behind a toggle,
never as the only option. The pending-device queue must show a badge count in the
navigation so discovery is noticed without hunting for it.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Routing oracle** — an independent naive evaluator computes expected stream membership
  for 10,000 generated messages against 50 generated stream definitions; assert exact
  agreement with the production router.
- **ReDoS suite** — feed known catastrophic-backtracking patterns and adversarial inputs.
  Assert every user-supplied regex is compiled with a timeout and that a malicious stream
  rule cannot stall ingestion.
- **Discovery idempotency** — 100k messages from one unknown source create exactly one
  pending record. Race 20 concurrent sources; assert no duplicates and no lost registrations.
- **Regression** — re-run the ingest benchmark with 20 active streams; throughput must
  hold ≥ 5,000 msg/sec.
- **Evidence:** routing oracle agreement report, ReDoS timeout proof, throughput with
  streams active.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Discovery flood** — 100,000 spoofed source IPs. Assert the pending queue is bounded,
  the database does not balloon, and an operator alert fires rather than the UI becoming
  unusable.
- **ReDoS in stream rules** — a user-authored catastrophic pattern must not stall the
  ingest path for other devices. Assert per-rule timeout and isolation.
- **IDOR** — device and group records accessed and modified by ID across scopes.
- **Stored XSS via device fields** — hostname and vendor come from the wire and are
  rendered in pickers, health cards, and dashboards. Test each.
- **Authorization on approval** — only Administrators approve pending devices; assert
  Operator and Read-Only are refused at the API, not just hidden in the UI.
- **Evidence:** flood containment results, ReDoS isolation proof, IDOR matrix.

## Definition of Done
Standard DoD, plus post-stream-routing throughput recorded in PROGRESS.md.

## Commit
`feat: phase 6 — device registry, discovery, groups, stream routing` → tag `v1.0.0-phase.6`
