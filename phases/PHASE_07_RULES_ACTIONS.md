# PHASE 7 — Rules & Actions Engine

## Context
Streams route messages. This phase makes the product *do* things — the Kiwi-style
filter→action engine that is the core operator feature. Actions have real-world side
effects, so rate limiting and cool-downs are requirements, not polish.

## Objective
A rules engine evaluated at ingest, with all action types implemented, rate-limited,
and audited.

## Build
1. **Rule model**: name, description, enabled, priority (ordered evaluation), filter
   expression (reuse the Phase 6 condition builder), ordered action list, optional
   time-of-day window, optional device-group restriction, hit counter, last-fired timestamp.
2. **Evaluation**: rules run in priority order at ingest. A `Suppress` action stops
   further rule processing for that message.
3. **Actions**, each with per-action rate limit and cool-down:
   - `SendEmail` — SMTP with TLS, templated subject and body with `{field}` substitution
   - `HttpWebhook` — JSON POST, custom headers, retry with exponential backoff, timeout
   - `RunScript` — executable + args, working directory, timeout-bounded, stdout/stderr
     captured to the audit log, non-zero exit recorded. Path allow-list enforced.
   - `ForwardSyslog` — UDP/TCP/TLS to another host, optional reformatting template
   - `WriteToFile` — named file, rotation by size and age
   - `WriteToOdbc` — external SQL destination, column mapping
   - `AddTag`, `RouteToStream`, `Suppress`
   - `RaiseNotification` — appears in the UI notification centre
4. **Escalation**: if a rule's condition fires N times in M minutes, run a second,
   different action list instead.
5. **Alert-storm protection**: a global outbound action budget per minute. On breach,
   collapse into a single summary notification rather than sending hundreds.
6. Credentials for email, webhooks, and ODBC read from the Phase 4 DPAPI secret store.
   Never persisted in the rule definition.
7. **Rule tester**: pick or paste a message, dry-run the rule set, see which rules match
   and which actions *would* run — without executing them.

## Do not build in this phase
Scheduled aggregation alerts — that is Phase 8. These are per-message rules only.

## Tests to write first
- Each action type integration-tested against a local stub: an in-process SMTP sink, an
  HTTP sink, a temp file, a local syslog receiver, a SQLite ODBC target, a script that
  echoes and exits 0 and a variant that exits 1.
- Priority test: rules fire in the correct order; `Suppress` halts the chain.
- Rate-limit test: 1,000 matching messages produce exactly the configured number of sends.
- Cool-down test with a virtual clock.
- Escalation test: Nth occurrence within the window triggers the escalation list once.
- Script sandbox test: an executable outside the allow-list is refused and audited.
- Secret test: assert no credential appears in logs, audit diffs, or exported rules.

## Verification — run these and paste output
```bash
dotnet test --filter "Rules|Action"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Ingest*"
```
Confirm ingest throughput holds at ≥ 5,000 msg/sec with 50 active rules. Actions must
execute **off** the ingest thread — assert a slow action does not stall ingestion.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task: **create a rule that emails on any critical message
from the core switches, and verify it works — without documentation.**
Required here: the visual condition builder is the default editor; **Test buttons on SMTP,
webhook, and the whole rule** (dry-run against a sample message, showing which actions
would fire without executing them); and a starter library of 8-10 pre-built rule
templates users can clone rather than author from scratch.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

Actions have real side effects. Validate failure paths harder than success paths.

- **Fault injection per action type** — SMTP server refusing connections, hanging, or
  returning 4xx/5xx; webhook returning 500, timing out, or returning 10MB; disk full on
  file write; ODBC target unreachable; script exceeding its timeout, exiting non-zero, or
  writing 100MB to stdout. **Every failure must be contained, retried per policy, logged,
  and must never stall ingestion.**
- **Ingest isolation proof** — configure an action that blocks for 60 s; assert ingest
  throughput is unaffected. This is the single most important test in the phase.
- **Rate-limit accuracy** — 10,000 matching messages produce exactly the configured
  number of sends, verified against the sink's own count, not our counter.
- **Idempotency** — a rule re-evaluated after a restart does not double-fire.
- **Script sandbox security** — path traversal in the executable path, argument injection,
  environment variable leakage, symlink escape. All must be refused and audited.
- **Mutation testing** on the rules evaluator, **≥ 70%**.
- **Evidence:** fault-injection matrix (action × failure mode), isolation benchmark,
  rate-limit sink counts, sandbox refusal log, mutation score.

## Security Validation (per `SECURITY_STANDARDS.md`)

Actions turn log content into outbound network calls and process execution. This is the
most dangerous phase in the product.

- **SSRF on the webhook action** — attempt `127.0.0.1`, `169.254.169.254` (cloud
  metadata), internal RFC1918 ranges, `file://`, `gopher://`, DNS rebinding, and redirect
  chains to internal hosts. Enforce a scheme allow-list (https, http), block link-local
  and metadata ranges by default, disable redirect following, and make any internal-range
  exception explicit configuration.
- **Command injection on the script action** — field substitution into arguments using
  `; & | $() \`\` %VAR%` and newline payloads. Arguments must be passed as an argument
  vector, never through a shell. Assert an executable outside the allow-list is refused,
  and test symlink escape and relative-path traversal.
- **Path traversal on the file action** — `../`, UNC paths, ADS (`file.txt:evil`),
  reserved Windows names (`CON`, `NUL`), and a hostname field containing separators.
- **SMTP header injection** — CRLF in a templated subject must not add headers or
  recipients.
- **Template injection** — assert the substitution engine cannot execute expressions or
  reach outside the event object.
- **Secret leakage** — configure every action with credentials, force every failure path,
  then grep logs, audit diffs, UI errors, and config bundle exports. Zero hits.
- **Forward-loop protection** — a `ForwardSyslog` action pointed at the collector's own
  listener must be detected, not amplified.
- **Threat model review #2** — egress boundary now exists and must be documented.
- **Evidence:** SSRF matrix, command-injection matrix, traversal matrix, secret scan,
  loop-detection proof.

## Definition of Done
Standard DoD, plus the slow-action isolation test passes, throughput is recorded, and a
first-time user can create a working email rule in under 3 minutes.

## Commit
`feat: phase 7 — rules engine, actions, rate limiting, escalation` → tag `v1.0.0-phase.7`
