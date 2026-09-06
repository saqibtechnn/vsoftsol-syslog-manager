# ASVS-checklist.md — OWASP ASVS 5.0, Level 2

SECURITY_STANDARDS.md §1. Every applicable L2 control is `implemented`, `planned` (with
the owning phase), or `n/a` (with a reason). This is the Phase 0 baseline; each phase
updates its rows and Phase 4 does a full V1–V4/V7/V14 verification pass.

Legend: **I** implemented · **P** planned · **N/A** not applicable

| V | Area | State | Notes / owning phase |
|---|---|---|---|
| **V1** | **Architecture, design, threat modelling** | | |
| V1.1 | SDLC documents security | I | CLAUDE.md, SECURITY_STANDARDS.md, TESTING_STANDARDS.md, this checklist |
| V1.2 | Authenticated components / least privilege | P | Dedicated low-privilege service account, data-dir ACLs — ADR 0006, enforced Phase 12 |
| V1.4 | Trusted enforcement points; fail closed | P | Single scope-filter chokepoint; authz denies on error — Phase 4 |
| V1.5 | Input/output trust boundaries defined | I | THREAT_MODEL.md B1–B5 |
| V1.6 | Threat model exists and is maintained | I | THREAT_MODEL.md, reviewed Phases 4/7/11 |
| V1.11 | Business-logic limits documented | I / P | Per-source ingest rate limiter + max message size + spill size cap (Phase 2, `IngestionOptions`); rule/action budgets Phase 7 |
| V1.14 | Segregation of components | I | `Core` I/O-free (fitness test); layered projects; two seams only |
| **V2** | **Authentication** | | |
| V2.1 | Password policy, no forced composition rules, length allowed | P | Phase 4 |
| V2.2 | Anti-automation: lockout, rate limiting | P | Account lockout, login rate limiting — Phase 4 |
| V2.4 | Credential storage: Argon2id with sane params | P | Phase 4 (`LocalAuthenticationProvider`) |
| V2.5 | Credential recovery does not reveal the current secret | P | Forced-change flow for seeded admin — Phase 4 |
| V2.7 | Constant-time verification; no user enumeration | P | `AuthenticationResult` hides unknown-user vs bad-password — Phase 4 |
| V2.x | External IdP / AD | P | `IAuthenticationProvider` seam only in v1 — ADR 0008 |
| **V3** | **Session management** | | |
| V3.1 | No session data in the URL | P | Phase 4 |
| V3.2 | Session generated server-side, regenerated on login | P | Phase 4 |
| V3.3 | Idle and absolute timeout; logout invalidates server-side | P | `UiSessionTimeoutMinutes` shape in Phase 0; enforced Phase 4 |
| V3.4 | Cookies: `Secure`, `HttpOnly`, `SameSite` | P | Phase 4; HTTPS-only host already in Phase 0 |
| **V4** | **Access control** | | |
| V4.1 | Enforced server-side, deny by default | P | Policy-based authz; scope filter fails closed — Phase 4 |
| V4.2 | No IDOR; object-level checks | P | Scope-bypass suite (by id, query, sort, export) — Phases 5, 6 |
| V4.3 | Admin interfaces need extra authz | P | Phase 4 |
| **V5** | **Validation, sanitisation, encoding** | | |
| V5.1 | Input validation with allow-lists | I / P | Schema CHECK constraints (facility/severity/protocol/parse_status) reject invalid rows at the store (Phase 1); config validation with `what/how-to-fix` messages — every UI phase |
| V5.2 | Untrusted data sanitised for the sink, not on ingest | I | Repository binds every parameter (SQL sink); NUL replaced only for the SQLite-TEXT sink while `raw_message` keeps the true bytes (Constraint 4); render sinks Phases 5/9/10 |
| V5.3.4 | SQL injection prevented by parameterisation | I | `SqliteLogRepository` — CWE-89 sweep + `ToFtsPhrase` quote-doubling; `SCS0002` build gate (Phase 1) |
| V5.3 | Output encoding per context (HTML, attr, JS, CSV, PDF) | P | Six output surfaces — Phases 5, 9, 10; CSV formula-injection guard — Phase 5 |
| V5.5 | Safe deserialization; no arbitrary types | P | Config-bundle import — Phase 11 |
| **V6** | **Stored cryptography** | | |
| V6.2 | Secrets encrypted at rest | P | DPAPI-backed secret store — Phase 4; never logged/diffed/exported |
| V6.4 | Key management / rotation documented | P | Phase 12 hardening guide |
| V6.x | No weak algorithms | I (gate) | `CA5350/5351/5358/5359` are build errors — `.editorconfig` |
| **V7** | **Error handling and logging** | | |
| V7.1 | No sensitive data in logs; log security events | I / P | Repository logs no message payloads — asserted (Phase 1); audit event list + writes — Phase 4 |
| V7.3.1 | Logs protected from tampering | I / P | `audit_log` `UPDATE`/`DELETE` blocked by `BEFORE` triggers (Phase 1); write path Phase 4 |
| V7.2 | Errors give a correlation id, not a stack trace, to users | I | `Error.razor` shows a correlation id; `DetailedErrors` only in Development |
| V7.3 | Logs protected from tampering | P | Audit log append-only + tamper-evident — Phase 4 |
| V7.4 | Time source is UTC and consistent | I | All timestamps UTC; convert at UI edge only |
| **V8** | **Data protection** | | |
| V8.2 | Sensitive data not cached client-side | P | Phase 4 |
| V8.3 | Least data in responses | P | Scoped queries — Phase 5 |
| **V9** | **Communications** | | |
| V9.1 | TLS everywhere for the UI | I | HTTPS-only host, HSTS configured, HTTP→HTTPS redirect (Phase 0); `A` grade target Phase 4/12 |
| V9.2 | Outbound TLS validated; no disabled cert checks | I (gate) | `CA5359` is a build error; webhook/SMTP TLS — Phase 7 |
| **V10** | **Malicious code** | | |
| V10.2 | No backdoor / debug endpoint / default credential | P | Asserted by test — Phases 4, 12 |
| V10.3 | Dependency integrity; SCA; SBOM | I | Central pinned versions, no floating ranges; `dotnet list --vulnerable` clean; CycloneDX SBOM in CI; Gitleaks full history |
| **V11** | **Business logic** | | |
| V11.1 | Sequential-step and rate-limit enforcement | I / P | Per-source ingest token-bucket rate limiter with throttle / drop-with-counter / quarantine (Phase 2, tested); rule/action budgets (Phase 7) |
| **V12** | **Files and resources** | | |
| V12.1 | Upload size / type limits | P | Config-bundle import limits — Phase 11 |
| V12.3 | No user input in file paths | P (gate) | Allow-listed destinations; path traversal tests — Phases 7, 10 |
| V12.4 | Files served with correct type, no execution | I | `X-Content-Type-Options: nosniff`; static files from `wwwroot` only |
| **V13** | **API / web service** | | |
| V13.1 | Same authz for all channels | P | Blazor circuit + any endpoint go through the same policies — Phase 4 |
| V13.2 | CSRF protection on state change | P | Anti-forgery — Phase 4 (`UseAntiforgery` wired Phase 0) |
| **V14** | **Configuration** | | |
| V14.1 | Build is repeatable and hardened | I | Deterministic + `ContinuousIntegrationBuild`; reproducibility check in CI; warnings-as-errors |
| V14.2 | No known-vulnerable dependencies | I | SCA gate; transitive pins for legacy `System.*` |
| V14.3 | No debug features in production | I | `DetailedErrors`/`AnalysisLevel` dev-only; exception handler + HSTS in non-dev |
| V14.4 | Security headers set | I | CSP, HSTS config, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options` — asserted by test; `unsafe-inline` removal tracked for Phase 4 |
| V14.5 | HTTP method / host allow-listing | I | `AllowedHosts: localhost`; bind to localhost or a named interface |

## Open L2 gaps carried out of Phase 0

- CSP still allows `'unsafe-inline'` on `style-src` (Blazor error UI). Tracked:
  known-issues.md, resolved in Phase 4 with nonces.
- Everything marked **P** — owned by the listed phase, re-verified there.
