# Phase 7 — security evidence

Per `SECURITY_STANDARDS.md` and the PHASE_07 "Security Validation" section. Actions turn
attacker-controllable log content into outbound network calls and process execution — the
most dangerous phase in the product. Failure paths are validated harder than success paths.

## Gates

| Gate | Result | Evidence |
|---|---|---|
| SAST (analyzers + Roslynator + SecurityCodeScan as errors) | PASS | `dotnet build -c Release` warning-clean, 14 projects |
| SCA (`dotnet list package --vulnerable --include-transitive`) | PASS | `sca-vulnerable.txt` — one new package (`System.Data.Odbc` 8.0.1, Microsoft-owned, MIT); no vulnerable / no floating version |
| Secrets scan | PASS | credentials referenced by **name** only; `ActionSecretLeakageTests` forces every failure path and greps `ActionResult.Detail` (audited) — zero hits |
| Branding literal guard | PASS | grep for brand values under `src/` returns only `BrandingInfo.g.cs` |

## PHASE_07 Security Validation

| Item | How verified (test) | Result |
|---|---|---|
| **SSRF — webhook** — `127.0.0.1`, `localhost`, `169.254.169.254` (cloud metadata), `[::1]`, `[fd00::1]`, `10/8`, `172.16/12`, `192.168/16`, `100.64/10`, `file://` / `gopher://` / `ftp://` | `WebhookSsrfTests` — `PrivateNetworkGuard` resolves the host and refuses **every** resolved private/loopback/link-local/metadata address **before a request is made**; non-http schemes rejected; redirect following disabled (`AllowAutoRedirect=false`, a 3xx is a permanent failure); a private target is reached **only** with both the per-action `AllowPrivateNetwork` flag **and** an explicit CIDR in the admin-configured host allow-list | PASS |
| **Command injection — script** — `; & \| $() \`\` %VAR%` and newlines in an argument | `ScriptSandboxTests` — `ProcessStartInfo.ArgumentList` (argument vector, `UseShellExecute=false`); the payload is echoed by the fixture as **one literal argv token**; an executable outside the allow-list, a relative / `..` path is refused; the program path is symlink-resolved (`ResolveLinkTarget`) and re-checked; the parent environment is **not** inherited (a set canary does not reach the child) | PASS |
| **Path traversal — file** — `../`, `..\`, UNC, ADS (`name:stream`), reserved names (`CON`), absolute paths, separators via `{hostname}` | `ActionExecutorTests.File_*` — `SafeFilePath.Resolve` rejects each; the final path must resolve under the configured base directory; a `{hostname}` substitution is reduced to a filename-safe token (runs of dots collapsed) and stays under the base dir | PASS |
| **SMTP header injection** — CR/LF in a templated subject / address | `ActionExecutorTests.Email_SubjectCrLfInjection…` — `FieldTemplate` strips control chars from substituted values; `EmailExecutor` additionally cuts the subject and every address at the first CR/LF; the payload lands on the Subject line as text, never as a new header, and no extra recipient appears | PASS |
| **Template injection** | `FieldTemplateTests` — a token is a single field lookup against `ConditionFields` (+ a small render-only set), reusing `EventFieldReader`. No expressions, no method calls, no property traversal. Unknown token → empty. Output capped at 64 KB | PASS |
| **Secret leakage** — every action with credentials, every failure path | `ActionSecretLeakageTests` — email auth failure, ODBC connection failure with `PWD=` appended to the connection string, and a missing secret: the value never appears in `ActionResult.Detail`; a missing secret is a *permanent* failure with a message that names the secret, not the value | PASS |
| **Forward-loop protection** | `ActionExecutorTests.Forward_ToOwnListener_IsRefusedAsALoop` — a `ForwardSyslog` target matching one of the collector's own listener endpoints (injected from `IngestionOptions`) is refused at compile time **and** at execute time; a literal loopback address on any local port is caught; nothing is sent | PASS |
| **Fault injection per action type** — refuse-connection / hang / 4xx / 5xx / oversize / timeout / disk-full / non-zero-exit / 100 MB-stdout | `ActionExecutorTests`, `ScriptSandboxTests`, `OdbcActionTests` — every failure is **contained** (never throws past the executor), **classified** (transient → back-off retry; permanent → dead-letter), **audited**, and **never stalls ingestion** (`RuleIngestIsolationTests`) | PASS |
| **Ingest isolation** — a 60 s-blocking action | `RuleIngestIsolationTests.SlowAction_DoesNotStallIngest` — 2,000 messages commit in ~0.2 s while a rule's webhook action would block; all 2,000 land in the outbox `pending` | PASS |
| **Rate-limit accuracy** — verified against the sink | `RuleRuntimeTests.Apply_RateLimit_ProducesExactlyTheConfiguredNumberOfDispatches` — 1,000 matching messages → exactly 5 dispatches, 995 rate-limited; the sliding window re-opens after the window | PASS |
| **Idempotency after restart** | `ActionOutboxTests` + `Migration005Tests` — the `UNIQUE (rule_id, event_id, action_index)` key on the outbox makes a re-enqueue a no-op; the outbox row is written transactionally with the event, so an un-committed (crash-replayed) event gets a new id and never collides | PASS |
| **Authorization** — rule CRUD, delete, action test | `RuleWebTests` — `SaveAsync` / `SetEnabledAsync` refused for Read-Only **at the service**; `DeleteAsync` Administrator-only; every mutation and every executed test audited | PASS |
| **Mutation testing ≥ 70 %** on the rules evaluator | BLOCKED on this SDK-only host (P3-3). Compensating: the **10,000-case rules-matcher oracle** (`RuleSetOracleTests`, 0 divergences), the ReDoS suite (inherited from ADR 0014), and the fault-injection matrix above. Carried to a CI host. | carried |
| DAST (OWASP ZAP) | NOT RUN — no browser/Docker (P4-1). The `/rules*` surfaces get compensating xUnit assertions against real Kestrel over HTTPS (`RuleWebTests`). Carried to Phase 12 / CI. | carried |
| Live SQLite-ODBC round-trip | NOT RUN — no ODBC driver on this VM (P7-3). `OdbcWriteExecutor` fully implemented + unit-tested; carried to Phase 12. | carried |

No Critical, High, or Medium findings. No `TODO(phase-N)` markers in shipping code.

## Threat-model review #2

`docs/security/THREAT_MODEL.md` — **B4 egress boundary** fully populated for the first time:
SSRF, command injection, path traversal, SMTP-header injection, template injection, secret
leakage in transit / on failure, and forward-loop amplification. See the review-log entry.
