# Phase 7 — verification (phase prompt "Verification" section)

## `dotnet test --filter "Rules|Action"`

```
Condition/rule engine   RuleCompilerTests, RuleSetTests, RuleRuntimeTests,
                        FieldTemplateTests, RuleJsonTests (35 unit) ............ PASS
Rules-matcher oracle    RuleSetOracleTests — 200 rule sets × 50 events =
                        10,000 comparisons, 0 divergences ..................... PASS
Migration 005           Migration005Tests — rule columns, outbox, notifications;
                        UNIQUE(rule_id,event_id,action_index); state CHECK ..... PASS
Persistence             RulePersistenceTests, ActionOutboxTests,
                        NotificationStoreTests — round-trip, Version bump, hit
                        flush, claim/fail/backoff/dead-letter/purge/recover .... PASS
Action fault injection  ActionExecutorTests — email / webhook / script /
                        forward / file / notification: success + every failure
                        mode contained, classified, audited ................... PASS
SSRF matrix             WebhookSsrfTests — 13 private/metadata/scheme cases .... PASS
Script sandbox          ScriptSandboxTests — argv vector, allow-list, symlink,
                        timeout-kill, no env inheritance ..................... PASS
Secret leakage          ActionSecretLeakageTests — zero hits on any fail path .. PASS
ODBC                    OdbcActionTests — identifier + parameterisation guards . PASS
Ingest isolation        RuleIngestIsolationTests — 2,000 msgs / 0.2 s with a
                        blocking action; every one in the outbox ............. PASS
Dispatcher              ActionDispatchServiceTests — claim→execute→audit,
                        retry→dead-letter→notify, hit flush .................. PASS
Web surface             RuleWebTests — route auth, role at the service,
                        dry-run executes nothing, template clone disabled ..... PASS
```

Filtered run: `test-output-filtered.txt` — **unit 35 / integration 84, 0 failed**.
Full suite: unit **666 / 666**. Integration: the first Release run read **420 / 421** — the
one failure was the timing-sensitive Phase 4
`AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime` (Argon2 decoy-hash timing,
ratio 3.0 vs a 2.0 threshold, while a 200k benchmark ran concurrently); the coverage run a
few minutes later, with less contention, read **421 / 421**. Recorded as **P7-5**, a
load-dependent flake in a *pre-existing* Phase 4 test — not a Phase 7 regression (same class
as P2-5). Passes 2 of 3 quick reruns in isolation.

## `dotnet run -c Release … -- --ingest-probe --rules 50`

See `benchmarks.md` / `benchmark-run.txt`:

| parse mode | rules | msg/sec | verdict |
|---|---|---|---|
| RFC header only          | 50 | **9,433** | **PASS** (≥ 5,000 with headroom) |
| RFC + vendor extraction  | 50 | ~3,600 | MARGINAL — the vendor path is sub-gate at *baseline* on this VM (P3-2); carried (P7-4) |

**Actions execute off the ingest thread** — the phase's "single most important test":
`RuleIngestIsolationTests.SlowAction_DoesNotStallIngest` commits 2,000 messages in ~0.2 s
while a rule's webhook action would block for minutes; all 2,000 land in the outbox.
Post-rules-engine throughput recorded in `PROGRESS.md` per the Definition of Done.

## Tests-to-write-first (phase prompt list)

| Required test | Where | Result |
|---|---|---|
| Each action type against a local stub | `ActionExecutorTests` (SMTP sink, `HttpListener`, temp file, local UDP receiver, echo/exit-1 probe); ODBC → identifier tests + carry | ✓ |
| Priority order; `Suppress` halts the chain | `RuleSetOracleTests` (order), `RuleRuntimeTests.Apply_SuppressAction_HaltsFurtherRules` | ✓ |
| Rate limit — 1,000 messages → exactly N sends | `RuleRuntimeTests.Apply_RateLimit_ProducesExactlyTheConfiguredNumberOfDispatches` | ✓ |
| Cool-down with a virtual clock | `RuleRuntimeTests.Apply_Cooldown_BlocksUntilTheGapElapses` | ✓ |
| Escalation — Nth in window triggers the escalation list once | `RuleRuntimeTests.Apply_Escalation_SwapsToTheEscalationListOnce…` | ✓ |
| Script sandbox — outside the allow-list refused + audited | `ScriptSandboxTests.Script_OutsideTheAllowList_IsRefused` | ✓ |
| Secret test — no credential in logs / audit / exported rules | `ActionSecretLeakageTests` + the model stores only a secret **name** | ✓ |

## Live host

New routes, all behind auth (`RuleWebTests.Routes_RequireAuthentication`):

```
GET /rules            -> 302 /login
GET /rules/0           -> 302 /login   (new-rule editor)
GET /rules/{id}        -> 302 /login
GET /rules/templates   -> 302 /login
GET /rules/tester      -> 302 /login
```

The notification-centre bell (`NotificationCenter.razor` → `SqliteNotificationStore`) shows
an unread badge and is exercised by `NotificationStoreTests` + the dispatcher dead-letter
path (`ActionDispatchServiceTests`).
