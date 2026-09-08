# ADR 0014 — Condition model in Core, stream routing on the ingest path

**Status:** Accepted (Phase 6)

## Context

PHASE_06 adds a device registry with auto-discovery and user-defined **streams** — named
views backed by a visual match rule (field / operator / value, AND/OR groups), the same
`ConditionBuilder` tree the Phase 4 design system already renders. Every ingested message
must be evaluated against every enabled stream **once, at ingest**, and the matching
stream ids stored in a link table, so the UI can page a stream without re-scanning.

Four constraints shape the design:

- **CLAUDE.md "Repository layout"** — `Core` does no I/O; `Data` owns SQLite; `Rules` owns
  rule evaluation. `LayeringTests` forbids `Data`→`Rules` and `Ingestion`→`Rules`.
- **CLAUDE.md "Two seams only"** — `ILogRepository`, `IAuthenticationProvider`. No new
  speculative interfaces.
- **CLAUDE.md Constraint 9 / SECURITY_STANDARDS.md** — every field from the network is
  hostile. A stream rule can contain a user-supplied regex (`Matches` operator). A
  catastrophic-backtracking pattern must not be able to stall or DoS the ingest path.
- **ADR 0005** — collector and UI run in **one process** in production, so an in-memory
  version counter is an authoritative cache-invalidation signal.

## Decision

### 1. The condition tree (`ConditionNode`) moves to `Core`.

`ConditionGroup` / `ConditionComparison` / `ConditionField` were Phase 4 Web types. They
move to `VSoftSol.Syslog.Core/Conditions/` unchanged (plus `[JsonPolymorphic]` with a
`"kind"` discriminator for persistence). Both the ingest-path evaluator (`Rules`) and the
Blazor builder (`Web`) now share one model. `ConditionFields` (the allow-list of
matchable fields, with aliases) lives beside it. Phases 7 (rule conditions) and 8 (alert
conditions) reuse the same tree.

### 2. Compilation and evaluation live in `Rules` (`ConditionCompiler`, `ConditionEvaluator`).

`ConditionCompiler.Compile(ConditionNode?)` returns a `ConditionCompileResult` — either a
`CompiledCondition` (an immutable tree of pre-resolved predicates and pre-built regexes) or
a list of human-readable errors. `ConditionEvaluator.Matches(CompiledCondition, SyslogEvent)`
is pure and allocation-light. Bad rules are a compile-time error surfaced to the operator,
never an ingest-time exception.

Guardrails, enforced at compile time: `MaxNodes = 200`, `MaxDepth = 12`, an empty group
compiles to a constant (`AND` of nothing = true, `OR` of nothing = false).

### 3. Regex uses `RegexOptions.NonBacktracking` — ReDoS is impossible by construction.

The `Matches` operator compiles to `new Regex(value, NonBacktracking | CultureInvariant,
250 ms timeout)`. `NonBacktracking` guarantees linear-time matching regardless of the
pattern, so catastrophic backtracking cannot occur. Patterns `NonBacktracking` cannot
represent (backreferences, lookaround) throw `NotSupportedException` at compile time and
are **rejected** with a "linear-time engine" message — the operator fixes the rule; the
ingest path never sees an un-vetted pattern. The 250 ms timeout is a belt-and-braces
backstop for pathological automaton sizes; a timeout at evaluation makes only that one
comparison return false (fail closed).

This differs from Phase 3 ADR 0011, which kept `NonBacktracking` as a *fallback* for
high-volume vendor extraction because of its ~40% throughput cost on hot regexes. For
stream routing the trade is reversed: correctness and safety of an admin-authored,
attacker-adjacent pattern outweigh the speed cost, and the benchmark still clears the
5,000 msg/sec gate with 20 streams (`docs/evidence/phase-06/benchmarks.md`).

### 4. `SyslogEvent.StreamIds` is a transient routing carrier, not a stored column.

`SyslogEvent` gains `IReadOnlyList<long> StreamIds` and `WithRouting(long? deviceId,
IReadOnlyList<long> streamIds)`. These are populated between parse and commit and consumed
by `SqliteLogRepository.AppendBatchAsync`, which writes `event_streams (event_id,
stream_id)` rows under the existing write lock (`INSERT OR IGNORE`). `StreamIds` is never
persisted on the `events` row and never read back onto it — the link table is the source
of truth. The pre-existing `linkParseFailure` safety net (route unparsed events to the
"Parse Failures" stream even with no enricher) is kept.

### 5. The bridge is a delegate in the composition root, not a seam.

`Ingestion` may not reference `Rules` or `Data`. `EventEnricher` is a
`delegate ValueTask<SyslogEvent>(SyslogEvent, CancellationToken)` that `IngestionPipeline`
takes as an optional constructor argument and invokes per batch, swallowing failures
(a message is never lost to an enrichment error — it commits unrouted). It is **not** a
seam interface: the pipeline neither knows nor cares that the delegate bridges `Data`
(`DeviceResolver`) and `Rules` (`StreamRouter`). Only `AddCollectorRuntime` in
`Service` — the single composition root, which already references everything — wires it.
This is the same "delegate for composition, not a seam" line ADR 0008 draws for
`EventEnricher`-style hooks.

### 6. `StreamRouterProvider` (Service) owns the compiled router and rebuilds on version change.

`SqliteStreamStore` bumps an in-process `long _version` (via `Interlocked`) on every
write. `StreamRouterProvider.GetAsync` rebuilds the `StreamRouter` only when
`_store.Version` has moved, under a `SemaphoreSlim`. Per ADR 0005 there is exactly one
process, so this counter is authoritative — no cross-process invalidation, no polling of
the DB on the hot path. Compile errors for individual streams are logged once per rebuild
and do not block the others.

### 7. Discovery is idempotent and flood-bounded (`DeviceResolver` + `SqliteDeviceStore`).

`DeviceResolver` (Data singleton) caches IP → device-id in a `ConcurrentDictionary`; a new
IP causes exactly one `INSERT OR IGNORE INTO device_ips` under the write lock (100k
messages from one new IP ⇒ 1 write). `device_ips.ip` carries a `UNIQUE` index, so
concurrent discovery of the same IP collapses to one row. When the pending-approval queue
is at `MaxPendingDevices` (default 500) the resolver stops creating records and increments
a drop counter — a spoofed-source flood cannot grow the table without bound or starve the
ingest path.

## Consequences

- The replaceable-strategy seam count is unchanged (still two).
- `Core` gains `System.Text.Json.Serialization` attributes only (no runtime dependency;
  it already uses `System.Text.Json` for saved searches).
- `Rules` gains `InternalsVisibleTo` the two test projects — the compiled tree is
  `internal` (an implementation detail); the oracle test needs to see it.
- The `events_fts` deferred-index watermark (ADR 0009) is untouched: routing writes to
  `event_streams`, not `events`, and does not disturb the FTS trigger path.
- Phases 7 and 8 inherit `ConditionNode` + `ConditionCompiler` + `ConditionEvaluator`
  wholesale; they add actions, not a second condition language.
