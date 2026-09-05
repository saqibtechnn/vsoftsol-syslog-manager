# PHASE 4 — UI Shell & Authentication

## Context
The collector works end to end and stores parsed events. Nobody can see them yet. This
phase builds the shell, the security model, and the audit trail that every later phase
writes into.

## Objective
An HTTPS Blazor Server application with role-based access control, scoped visibility,
and an immutable audit log.

## Build
1. Blazor Server host, HTTPS only, bound to localhost or a configured LAN interface.
   HSTS, secure cookies, anti-forgery, and a strict Content-Security-Policy.
2. `LocalAuthenticationProvider` implementing `IAuthenticationProvider`, with Argon2id
   password hashing and configurable parameters.
3. Roles: **Administrator**, **Operator**, **Read-Only**, **Auditor**. Enforce with
   policy-based authorization, not role-string checks scattered through components.
4. **Scoped visibility**: a role assignment carries a set of visible streams and device
   groups. Every data query goes through a scope filter — build this as a single
   chokepoint so no future page can bypass it.
5. Account lockout after N failed attempts, configurable session timeout, forced
   password change on first login for the seeded admin.
6. **Audit log** — append-only, no update or delete path in the repository, separately
   retained. Every login attempt, config change (with before/after JSON diff), user
   management action, and export is recorded with actor, timestamp, and source IP.
7. Application shell: navigation, page layout, theme, notification centre placeholder,
   and a settings area scaffolded for later phases to fill.
8. DPAPI-backed secret storage service for SMTP and webhook credentials, used from
   Phase 7 onward. Secrets never appear in logs or the audit diff.
9. **Design system — build this now, every later phase consumes it.** Read
   `UX_STANDARDS.md` first. Deliver: one table component (virtualized, sortable, column
   chooser, empty state), one form component (inline validation, Basic/Advanced
   disclosure, unsaved-changes guard), one modal, one confirmation dialog, one toast
   service, one skeleton loader, one empty-state component taking title/explanation/
   primary-action, and one visual condition builder (field / operator / value rows with
   AND-OR grouping) reused by Phases 6, 7, and 8.
10. Fixed navigation: Dashboards · Search · Devices · Streams · Rules · Alerts · Reports ·
    Settings. Global time-range picker in a fixed position. Notification centre.
11. Keyboard map: `/` focuses search, `Esc` closes modals, `Enter` submits,
    arrows navigate grids, `?` opens the shortcut list.
12. Severity colour tokens defined once, always paired with a text label or icon.
    WCAG 2.1 AA contrast, visible focus rings, labelled inputs.
13. **Branding consumption** per `BRANDING.md`: logo on the login page and in the header
    (collapsing to the square mark on narrow viewports), favicon, page titles as
    `<Page> — <productName>`, and an About page showing logo, version, build date, vendor
    URL, and copyright. All values from `BrandingInfo`, all colours from the
    `--brand-primary` / `--brand-accent` CSS properties. No literals.

## Do not build in this phase
Search, dashboards, rules, device management pages. Only the shell and security.

## Tests to write first
- Authorization matrix test: for each of the 4 roles × each protected endpoint, assert
  allow or deny. This test must be exhaustive and must fail when a new page is added
  without a policy.
- Scope filter test: a user scoped to stream A cannot retrieve an event in stream B by
  any query path.
- Audit test: perform a config change, assert an audit row with correct before/after.
- Audit immutability test: assert update and delete attempts on `audit_log` fail.
- Lockout test.

## Verification — run these and paste output
```bash
dotnet test --filter "Authorization|Audit|Scope"
dotnet run --project src/VSoftSol.Syslog.Web
```
Manually confirm: HTTPS redirect works, self-signed certificate is generated, login
succeeds, forced password change fires, and an unauthenticated request to a protected
page redirects rather than 500s.

## UX gate (required — see `UX_STANDARDS.md`)
This phase sets the vocabulary for the whole UI, so the gate is strictest here.
Run all five checks and record results in PROGRESS.md. Additionally: confirm the design
system components are genuinely reusable by building the login and user-management
screens **entirely** from them. If either screen needs a one-off component, the design
system is wrong — fix it now rather than in Phase 9.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Exhaustive authorization matrix** — 4 roles × every protected route × every HTTP verb,
  generated from route discovery so **a new page added without a policy fails the build**.
  A hand-maintained list will rot within two phases.
- **Security suite** — session fixation, CSRF token absence and reuse, cookie flags
  (Secure, HttpOnly, SameSite), concurrent session handling, password reset flow, timing
  attack on login (assert constant-time comparison), lockout bypass attempts.
- **Scope-bypass suite** — for each data access path, attempt to reach out-of-scope data
  by direct ID, by query parameter, by sort field, and by export. All must be denied.
- **Audit immutability** — attempt UPDATE and DELETE on `audit_log` through the
  repository, through raw SQL, and through the API. All must fail.
- **Secret leakage scan** — trigger every log path with credentials configured; grep the
  log files, audit diffs, and HTTP responses for the secret value. Zero hits required.
- **Accessibility** — automated axe-core scan on every page, zero critical or serious
  violations, plus manual keyboard-only traversal.
- **Evidence:** authorization matrix output (all combinations, pass/deny), axe report,
  secret-scan result, UX gate results.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **ASVS L2 verification** for V1 (architecture), V2 (authentication), V3 (session),
  V4 (access control), V7 (error/logging), V14 (config). Record each control's result.
- **DAST** — OWASP ZAP full scan against the running UI. No High or Medium findings.
- **Authentication attacks** — credential stuffing, brute force past the lockout,
  username enumeration via timing and error text, password reset abuse, Argon2id parameter
  verification, and constant-time comparison.
- **Session attacks** — fixation, hijacking, logout not invalidating server-side,
  concurrent session policy, cookie flags (Secure, HttpOnly, SameSite=Strict), absolute
  and idle timeout.
- **Access control** — vertical escalation attempts per role pair, horizontal IDOR on
  every entity by ID, forced browsing to admin routes, and HTTP verb tampering.
- **Security headers** — CSP without `unsafe-inline`, HSTS, X-Content-Type-Options,
  Referrer-Policy, frame-ancestors. Asserted by test, not by inspection.
- **Audit tamper attempts** through repository, raw SQL, and API. All must fail.
- **Threat model review #1** — update boundaries now that the UI exists.
- **Evidence:** ASVS results, ZAP report, header assertions, escalation matrix.

## Definition of Done
Standard DoD, plus the authorization matrix test covers every protected route, and no
later phase will need to invent a new table, form, or modal.

## Commit
`feat: phase 4 — blazor shell, rbac, scoped visibility, audit log` → tag `v1.0.0-phase.4`
