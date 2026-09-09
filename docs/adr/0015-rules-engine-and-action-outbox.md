# ADR 0015 — Rules engine: model in Core, executors in Rules, a crash-safe action outbox

**Status:** Accepted (Phase 7)

## Context

PHASE_07 adds the filter → action engine — the core operator feature. Rules are evaluated
against every message at ingest, in priority order; a matching rule runs an ordered list of
actions with real side effects (SMTP, HTTP, process execution, file writes, ODBC, syslog
forwarding). Constraints:

- **CLAUDE.md "Repository layout"** — `Core` does no I/O; `Rules` owns rule evaluation;
  `LayeringTests` forbids `Rules`→`Data`, `Ingestion`→`Rules`/`Data`.
- **CLAUDE.md "Two seams only"** — `ILogRepository`, `IAuthenticationProvider`.
- **CLAUDE.md Constraint 3** — never lose a message; ingestion survives stalls and kills.
- **CLAUDE.md Constraint 9 / SECURITY_STANDARDS.md** — every field is hostile; actions turn
  attacker-controllable content into outbound calls and process execution.
- **PHASE_07** — "Actions must execute **off** the ingest thread"; "a re-evaluated rule
  after a restart does not double-fire"; per-action rate limit + cool-down; escalation;
  global alert-storm budget.
- **ADR 0005** — collector + UI in one process, so an in-process version counter is
  authoritative.

This ADR reuses everything ADR 0014 established for streams: the polymorphic JSON model in
`Core`, the compile/evaluate split in `Rules`, and the version-counter provider in
`Service`.

## Decision

### 1. The action model (`RuleAction`) and `RuleDefinition` live in `Core`.

`RuleAction` is a `[JsonPolymorphic("kind")]` hierarchy (10 derived types) that round-trips
to `rules.actions_json`, exactly like `ConditionNode` (ADR 0014). `RuleDefinition` carries
the filter (`ConditionGroup`, reused), the ordered action list, an optional
`TimeOfDayWindow`, a device-group id list, and an optional `EscalationPolicy`. Derived
metadata (`IsSideEffecting`, a display label, the discriminator token) is a **static helper
`RuleActionInfo`**, not properties on the model — a `[JsonIgnore]` override of an abstract
property is not reliably honoured by `System.Text.Json` and leaked a duplicate `kind` key.

**Credentials are never stored on an action** — only the *name* of a Phase 4 DPAPI secret
(`SecretName`). A config-bundle export carries the name, never the value.

### 2. Compilation, evaluation, the runtime, and all executors live in `Rules`.

- `RuleCompiler` → `CompiledRule` / `CompiledRuleSet` — validates the filter (via the Phase 6
  `ConditionCompiler`; regexes are `NonBacktracking`, ADR 0014) and every action (URL
  scheme, executable allow-list, file-name shape, ODBC identifiers, template fields, loop
  target). First of two layers — every executor re-checks its own inputs at run time.
- `RuleSet.Match(event, groupIds, now)` — pure; the rules whose filter, time window, and
  device-group scope all admit the event, priority order. Does **not** decide rate limits,
  cool-downs, escalation, or the `Suppress` halt.
- `RuleRuntime.Apply(matched, event)` — stateful, `TimeProvider`-driven: per-action
  token-bucket rate limiter + cool-down, per-rule escalation window (chooses the normal or
  escalation action list, once per window), the global per-minute outbound budget with a
  storm collapse to one summary notification. In-memory and process-local; the counters
  reset on restart, which is safe — message-level idempotency is the outbox's job, not this.
- `Rules/Actions/` — one `IActionExecutor` per side-effecting kind (`internal`, a closed
  set, not a seam) + guards (`PrivateNetworkGuard`, `ExecutablePathGuard`, `SafeFilePath`).
  Executors use only BCL — `System.Net.Mail`, `HttpClient`, `System.Diagnostics.Process`,
  sockets — plus one Microsoft package, `System.Data.Odbc`. No project reference. Secret
  resolution, notification raising, and the local-listener list arrive as **delegates**
  (`SecretResolver`, `NotificationSink`) from the composition root — the same
  "delegate for composition, not a seam" line ADR 0008 / 0014 draw for `EventEnricher`.

### 3. `System.Net.Mail.SmtpClient` for SMTP.

Marked obsolete (`SYSLIB0014`) but it is the only dependency-free BCL SMTP client, and
Constraint 2 forbids adding a runtime dependency (MailKit brings `MimeKit` + a BouncyCastle
transitive to keep clean forever). The one warning is suppressed with a scoped `#pragma` in
`EmailExecutor.cs`. TLS is `EnableSsl`; CR/LF is stripped from the subject and every address
(SMTP-header-injection defence); recipients are config, never templated.

### 4. Side-effecting actions go through a **crash-safe outbox table**, not an in-memory queue.

`rule_action_queue` (migration 005): one row per matched side-effecting action, written **in
the same transaction as the event** by `SqliteLogRepository.AppendBatchAsync` from a
transient `SyslogEvent.PendingActions` carrier (same shape as `StreamIds`, ADR 0014). A
`UNIQUE (rule_id, event_id, action_index)` key makes a re-evaluated event idempotent — a
restart cannot double-fire. `ActionDispatchService` (a `BackgroundService` in `Service`,
collector host only) claims rows atomically (`UPDATE … RETURNING`), executes up to N in
parallel, retries transient failures with exponential back-off, dead-letters after
`MaxAttempts` (and raises an operator notification), recovers `running` rows abandoned by a
crashed dispatcher, flushes rule hit counters, and purges completed rows.

**Why an outbox, not a `Channel<T>`:** the phase requires crash-safe at-least-once with
idempotency after restart and per-policy retry. A persisted queue gives all three with a
natural dedup key, survives a hard kill, and rate-limits by claim. The cost is one extra
`INSERT OR IGNORE` per *matched* rule-action inside the existing event transaction — most
events match 0–1 rules, so the ingest-path cost is small (measured:
`docs/evidence/phase-07/benchmarks.md`).

### 5. Inline actions apply before commit; everything else is off-thread.

`AddTag` → an `event_fields` "tag" row; `RouteToStream` → an extra stream id;
`Suppress` → stops the rule chain for that message (storage is unaffected). These are
computed in the enricher and folded into the event via `SyslogEvent.WithRuleOutcome`. Every
other action is enqueued. The enricher never blocks on an action —
`RuleIngestIsolationTests` proves 2,000 messages commit in ~0.2 s with a rule whose webhook
would block.

### 6. `RuleSetProvider` (Service) owns the compiled rule set and rebuilds on version change.

Mirrors `StreamRouterProvider` (ADR 0014). `SqliteRuleStore` bumps a `long Version` on every
write (not on a hit-count flush); the provider rebuilds under a `SemaphoreSlim` only when
it moves. Per ADR 0005 the counter is authoritative — no cross-process invalidation.

### 7. SSRF / command-injection / traversal / header-injection / template-injection defences.

The egress boundary (THREAT_MODEL B4) is now real. See
`docs/evidence/phase-07/security/README.md` for the matrix. Highlights: the webhook resolves
its host and rejects **every** resolved private/loopback/link-local/metadata address unless
the operator allow-lists that CIDR *and* sets a per-action opt-in; redirects are disabled;
scripts run through `ProcessStartInfo.ArgumentList` (never a shell) with a symlink-resolved
allow-listed path and a minimal environment; file writes are confined to a base directory
with reserved-name / ADS / traversal rejection; a `ForwardSyslog` target matching a local
listener is refused as a loop.

## Consequences

- The replaceable-strategy seam count is unchanged (still two).
- `Core` gains `System.Text.Json.Serialization` attributes only.
- `Rules` gains one package (`System.Data.Odbc`, MIT, Microsoft-owned) and
  `InternalsVisibleTo` the test projects (the compiled tree + executors are `internal`).
- A new `tests/VSoftSol.Syslog.ActionProbe` fixture exe for the script-sandbox tests.
- Phase 8 alerts reuse `ConditionNode` + `RuleAction` + the executors + the outbox; they
  add scheduled aggregation, not a second action language.
