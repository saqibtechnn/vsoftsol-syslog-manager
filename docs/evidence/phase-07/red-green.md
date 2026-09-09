# Phase 7 — red / green log

Every new test was observed failing for the correct reason before the implementation
existed (TESTING_STANDARDS §2.1).

---

## Slice A — rule model (Core) + engine (Rules): compiler, matcher, runtime, templating

New: `Core/Rules/` (`RuleAction` hierarchy, `RuleDefinition`, `TimeOfDayWindow`,
`EscalationPolicy`, `ActionThrottle`, `NotificationLevel`); `Rules/Rules/`
(`RuleCompiler` → `CompiledRule`/`CompiledRuleSet`, `RuleSet` pure matcher, `RuleRuntime`
stateful rate-limit / cool-down / escalation / storm); `Rules/Templating/FieldTemplate`.

**RED — 2026-09-08** — `RuleCompiler.Compile`, `RuleSet.Match`, `RuleRuntime.Apply`,
`FieldTemplate.{Render,TryValidate}` each shipped as `=> throw new
NotImplementedException("red-green")` (stub files):

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Rules.Rule|FullyQualifiedName~Rules.FieldTemplate"
Failed!  - Failed: 33, Passed: 0, Total: 33   (all: System.NotImplementedException : red-green)
```

Test files:
- `FieldTemplateTests` — `{field}` substitution, `{{`/`}}` literals, unknown-token→empty,
  CR/LF stripped from substituted values (SMTP-injection defence) but tabs kept, 64 KB cap,
  `TryValidate` rejects unknown fields / unbalanced braces.
- `RuleCompilerTests` — valid rule; no actions; webhook non-http scheme; script outside the
  allow-list; script relative/`..` path; forward-to-own-listener (loop); file-name
  traversal; ODBC non-identifier table; unknown template field; escalation with no actions.
- `RuleSetTests` — priority order; no-filter matches everything; time-of-day window
  (incl. wrap-midnight); device-group restriction.
- `RuleRuntimeTests` — `Suppress` halts the chain; inline actions produce tags/stream ids
  not dispatches; **rate limit → exactly N dispatches over 1,000 messages** (5 sent, 995
  limited); sliding window re-opens; cool-down with a virtual clock; escalation swaps to
  the escalation list once when the threshold is hit in-window; global budget collapses
  the excess into a single summary.

**GREEN — 2026-09-08**

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Rules.Rule|FullyQualifiedName~Rules.FieldTemplate"
Passed!  - Failed: 0, Passed: 33
```

### Slice A — rules-matcher oracle

`RuleSetOracleTests` — an independent naive matcher (reusing the Phase 6
`NaiveConditionMatcher` for the filter) computes the expected matched-rule set (priority
order, filter + time window + device group) for **200 generated rule sets × 50 events =
10,000 comparisons**; `RuleSet.Match` agrees exactly.

**RED** — stub `RuleSet.Match` threw (part of the 33 above).
**GREEN** — `Passed! 1` — 10,000 comparisons, **0 divergences**.

---

## Slice B — persistence: migration 005, rule store, action outbox, notification store, templates

New: `Migrations/Scripts/005_rules_actions.sql` (rule columns + `rule_action_queue` outbox +
`notifications`); `Data/Rules/` (`RuleJson`, `SqliteRuleStore` with a `Version` counter,
`SqliteActionOutbox`); `Data/Notifications/SqliteNotificationStore`;
`Data/Seed/DefaultRuleTemplates` (10 starter templates); `Core/Rules/PendingRuleAction`;
`SyslogEvent.WithRuleOutcome` / `.PendingActions`; `SqliteLogRepository.AppendBatchAsync`
writes the outbox rows in the event transaction.

**RED — 2026-09-09** — migration `005` held back (removed from `Migrations/Scripts/`):

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~IntegrationTests.Rules"
Failed!  - Failed: 13, Passed: 2, Total: 15
  (no such table: rule_action_queue / no such column: rules.time_window_json)
```
Plus `RuleJsonTests.Actions_RoundTripPolymorphically` observed red — the first serialisation
emitted a **duplicate `kind` key** (the `[JsonIgnore]` abstract `Kind` / `IsSideEffecting`
properties were not honoured on the concrete overrides); moved to a static `RuleActionInfo`
helper so nothing derived leaks into `actions_json`.

**GREEN — 2026-09-09**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~IntegrationTests.Rules"
Passed!  - Failed: 0, Passed: 15
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~UnitTests.Rules"
Passed!  - Failed: 0, Passed: 34
```

- `Migration005Tests` — the new columns/tables; `rule_action_queue` UNIQUE
  `(rule_id, event_id, action_index)` makes a re-enqueue idempotent; the `state` CHECK
  rejects an unknown value.
- `RulePersistenceTests` — every rule field round-trips (filter, polymorphic actions,
  per-action throttle, window with days, group ids, escalation, secret **name** only);
  `Version` bumps on every write but **not** on a hit-count flush; delete refuses system
  rules; the 10 `DefaultRuleTemplates` all compile against a filled-in copy.
- `ActionOutboxTests` — the repository writes outbox rows in the event transaction; a
  duplicate enqueue is ignored; claim → fail → back-off → reclaim (virtual clock);
  dead-letter + purge; stale-`running` recovery.
- `NotificationStoreTests` — raise / list (newest first) / count-unread / mark-read /
  dismiss; hostile title stored verbatim (encoded at render); blank title rejected.
- `RuleJsonTests` — polymorphic action list round-trips with no duplicate discriminator.

---

## Slice C — the seven side-effecting executors + their guards

New: `Rules/Actions/` — `IActionExecutor` + `ActionExecutorRegistry`, `EmailExecutor`
(`System.Net.Mail`), `WebhookExecutor` (+ `PrivateNetworkGuard`), `ScriptExecutor`
(+ `ExecutablePathGuard`, minimal environment), `SyslogForwardExecutor` (+ loop guard),
`FileWriteExecutor` (+ `SafeFilePath`, size/age rotation), `OdbcWriteExecutor`,
`NotificationExecutor`. New fixture project `tests/VSoftSol.Syslog.ActionProbe`
(`action-probe` — echoes its argument vector + environment, exits with a chosen code).

**RED — 2026-09-09** — `ActionExecutorRegistry.ExecuteAsync` shipped as
`=> throw new NotImplementedException("red-green")` (stub file):

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~ActionExecutorTests|FullyQualifiedName~WebhookSsrfTests|FullyQualifiedName~ScriptSandboxTests|FullyQualifiedName~ActionSecretLeakageTests"
Failed!  - Failed: 44, Passed: 0, Total: 44   (all: System.NotImplementedException : red-green)
```

**GREEN — 2026-09-09**

```
… ActionExecutorTests | WebhookSsrfTests | ScriptSandboxTests | ActionSecretLeakageTests   Passed!  44
… OdbcActionTests                                                                          Passed!  4
```

- **Fault injection** (`ActionExecutorTests`) — email: deliver / refuse-connection→transient
  / 5xx→permanent; **CR/LF subject injection cannot add a header or a recipient**; webhook:
  deliver / 500→transient / 400→permanent / **1 s timeout does not hang the caller** /
  **redirect refused, not followed**; forward: UDP delivered / **loop to own listener
  refused, nothing sent**; file: append + **rotate by size** / traversal matrix
  (`../`, `..\`, absolute, UNC, ADS, `CON`) all refused / a `{hostname}` with separators is
  sanitised and stays under the base dir; notification via the sink.
- **SSRF matrix** (`WebhookSsrfTests`) — `127.0.0.1`, `localhost`, `169.254.169.254`,
  `[::1]`, `[fd00::1]`, `10/8`, `172.16/12`, `192.168/16`, `100.64/10` all refused before a
  request; non-http schemes refused; a private target reached **only** with both the
  per-action opt-in flag **and** an explicit CIDR in the host allow-list.
- **Script sandbox** (`ScriptSandboxTests`) — exit 0 / non-zero→permanent /
  **1 s timeout → killed, transient** / executable outside the allow-list refused /
  relative + `..` refused / **`; rm -rf / && curl … | sh $(whoami) \`id\`` passed as one
  literal argv token** / **the parent environment is not inherited** (a set env canary does
  not reach the child).
- **Secret leakage** (`ActionSecretLeakageTests`) — email auth failure, ODBC connection
  failure with `PWD=` appended, and a missing secret: the secret value never appears in
  `ActionResult.Detail` (which the dispatcher audits).
- **ODBC** (`OdbcActionTests`) — non-identifier table / column refused before any
  connection; a SQL-metacharacter templated value is bound, not concatenated (the failure
  is a connection error, `DROP TABLE` never in the detail). Live SQLite-ODBC round-trip →
  Phase 12 (P7-3, no driver on this VM).

---

## Slice D — ingest hook + dispatcher: `RuleSetProvider`, `ActionDispatchService`, wiring

New: `Service/Hosting/` — `RuleSetProvider` (rebuilds the compiled `RuleSet` on
`SqliteRuleStore.Version`, mirrors `StreamRouterProvider`), `RuleHitTracker` (in-memory hit
accumulation), `ActionDispatchOptions`, `ActionDispatchService` (`BackgroundService`:
claim → execute → audit → state, retry with back-off, dead-letter, hit flush, purge).
`SyslogPlatformExtensions` extends the ingest `EventEnricher` to evaluate the rule set,
apply inline actions, and enqueue side-effecting ones; registers the dispatcher.
`DeviceResolver.ResolveGroupsAsync` (cached) for device-group-restricted rules.

**RED — 2026-09-09** — `RuleSet.Match` stubbed to always return `[]` (stub file):

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~RuleIngestIsolationTests|FullyQualifiedName~ActionDispatchServiceTests"
Failed!  - Failed: 3, Passed: 2   (no rule ever matches → nothing enqueued)
```

**GREEN — 2026-09-09**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~IntegrationTests.Rules"
Passed!  - Failed: 0, Passed: 68
```

- `RuleIngestIsolationTests` — **the phase's most important test**: a rule whose webhook
  action would block is configured; **2,000 messages commit in ~0.2 s (~9,000 msg/sec) and
  every one produces a `pending` outbox row** — the blocking action never touched the ingest
  thread. Also: inline `AddTag` writes its `event_fields` row before commit while the
  side-effecting `RaiseNotification` is enqueued.
- `ActionDispatchServiceTests` — the dispatcher executes a pending webhook, marks it `done`,
  audits `action.fired`; a transient failure retries with exponential back-off (virtual
  clock) then **dead-letters after `MaxAttempts` and raises an operator notification**;
  rule hit counters flush to `rules.hit_count`.

GREEN-phase fix: `{}` (empty braces, common in JSON bodies) now renders/validates as a
literal `{}` rather than an empty-token error.

---

## Slice E — web: rule list / editor / templates / tester, notification centre

New: `Web/Rules/RuleAdminService` (+ `RuleActionResult`, `RuleDryRun`),
`Web/Notifications/NotificationService`; pages `Components/Pages/Rules/`
(`Rules.razor` list + enable toggle + hit count, `RuleEditor.razor` — `ConditionBuilder`
filter + per-action `RuleActionEditor` cards + time window + group restriction + escalation
+ Test buttons, `RuleTemplates.razor`, `RuleTester.razor`); `NotificationCenter.razor`
wired to the real store; the `/rules` `ComingSoon` placeholder removed; `ds.css` Phase 7
block.

**RED — 2026-09-09** — the `RuleAdminService` role checks in `SaveAsync` / `SetEnabledAsync`
/ `DeleteAsync` short-circuited:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~RuleWebTests"
Failed!  - Failed: 2, Passed: 7
  Save_IsRefusedForReadOnly_AtTheService   — Read-Only save succeeded
  Delete_IsAdministratorOnly               — Operator delete succeeded
```
(route-auth, dry-run-executes-nothing, template-clone, test-action are structural / behavioural
and stayed green under the break.)

**GREEN — 2026-09-09**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~RuleWebTests"
Passed!  - Failed: 0, Passed: 9
```

- `RuleWebTests` — `/rules`, `/rules/0`, `/rules/templates`, `/rules/tester` require auth;
  `SaveAsync` refused for Read-Only **at the service**; `DeleteAsync` is Administrator-only;
  **the dry-run executes nothing** (no outbox rows, no `action.fired` audit, just the
  would-fire report); cloning a template creates a **disabled** rule; the per-action Test
  button with `reallyExecute: true` runs the executor and writes an `action.tested` audit
  row.

## Full regression (Debug)

```
Unit         Passed!  … (UnitTests.Rules 34; full suite green)
Integration  Passed!  … (IntegrationTests.Rules 68 + RuleWebTests 9; full suite green)
```
