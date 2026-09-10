# Phase 8 — security evidence

PHASE_08 Security Validation. The threat model is **not** reviewed this phase (the scheduled
reviews are Phases 4, 7, and 11 — SECURITY_STANDARDS §3); the new/changed entries below are
recorded for the Phase 11 review.

## 1. Notification flooding as an attack

> "An attacker who can generate log events can weaponise the alerting system against the
> administrator's inbox or a downstream webhook."

| Defence | Evidence |
|---|---|
| per-alert dedup — one open instance per `(alert, group)` | `AlertEvaluationServiceTests.Dedup_ConditionTrueForTenEvaluations_ProducesOneOpenInstance`; `Migration006Tests.AlertInstances_PartialUniqueIndex_AllowsOnlyOneOpenInstancePerGroup` |
| a 40,000-event flood → 1 firing, 1 notification | `AlertStormContainmentTests.AFloodOfMatchingEvents_FiresTheAlertOnce_AndNotifiesOnce` |
| global `Alerts:GlobalActionsPerMinute` budget + storm-collapse to one summary | `AlertStormContainmentTests.ManyDistinctFiringsAtOnce_AreCollapsedToASummary_ByTheGlobalBudget`; `AlertRuntimeTests` (6 cases) |
| per-alert re-notify interval — a firing alert does not re-send every tick | `AlertReNotifyTests.FiringAlert_ReNotifiesOnlyOnTheConfiguredInterval` |
| per-action rate limit + cool-down | `AlertRuntimeTests.Reserve_Cooldown_* / Reserve_RateLimit_*` |

See `storm-containment.md`.

## 2. Information disclosure in alert content

> "A notification must never leak an event from a stream the recipient cannot see."

`AlertSecurityTests.TriggeringEvents_OutsideTheViewersScope_AreNotDisclosed`:

- an event routed only to stream **B**; a firing instance records it as a trigger event.
- a viewer scoped to stream **A** opens the alert-history detail → the trigger event comes
  back `Visible = false`, `Message = ""`, `Hostname = null` — the history UI shows
  *"#N (outside your visible scope)"* with **no id oracle beyond the number and no body**.
- an unrestricted viewer sees the full event.

Every triggering-event lookup in `AlertAdminService.GetInstanceAsync` goes through
`ScopedEventReader.GetByIdAsync`, the Phase 4 scope chokepoint. Instance *listings*
(`ListOpenAsync` / `ListHistoryAsync`) are filtered by `AlertAdminService.ScopeAllows`: an
alert restricted to streams/groups the viewer cannot see is hidden; an estate-wide alert
(no restriction) is visible to all — its existence is not sensitive and its events are
still scope-checked on drill-down.

Alert *action* recipients (an email address, a webhook URL) are chosen by the alert author,
who is an Administrator or Operator with full visibility — that is a configuration decision,
not a per-viewer scope surface. Recorded for the Phase 11 review.

## 3. Stored XSS in alert fields + HTML email

> "Descriptions, remediation notes, and triggering-event previews render in the notification
> centre and in emails. Test HTML email escaping specifically."

`AlertSecurityTests.HostileAlertFields_AreStoredByteIdentical_AndNeverExecutedInATemplate`:

- an alert name / description / remediation of
  `<script>alert('xss')</script> <img src=x onerror=alert(1)>` is stored **byte-identical**
  (`AlertRow.Description == payload`) — encoding happens at render, never at ingest
  (Constraint 9).
- the synthetic-event field template `{field.alert_name}` substitutes the hostile string as
  **literal text** — `FieldTemplate.Render` is a single-field lookup, not an expression
  engine (Phase 7 template-injection defence, still in force), and strips control characters.
- Razor auto-encodes all interpolated values on the notification centre / history pages
  (`@n.Title`, `@t.Note`, `@v.AlertName`, …).

**HTML email:** the `EmailExecutor` sends **plain-text** bodies (`MailMessage.IsBodyHtml`
defaults false and is not set) — there is no HTML email surface to escape. The alert body
is `FieldTemplate.Render`ed text with control characters stripped.
`AlertSecurityTests.AlertEmailBody_IsPlainText_WithControlCharactersStripped` asserts a
hostname of `host\r\nBcc: attacker@evil.com` renders with **no CR/LF** into the templated
subject — a folded SMTP header cannot be injected. (The Phase 7 `EmailExecutor.HeaderSafe`
adds a second cut-at-first-CRLF layer; unchanged.)

## 4. Authorization on the lifecycle

> "Read-Only cannot acknowledge or resolve, and ack/resolve actions are audited with the
> true actor."

`AlertWebTests`:

- `AcknowledgeAndResolve_AreRefusedFor(Role.ReadOnly)` / `(Role.Auditor)` — both refused
  **at the service** (`AlertAdminService.TransitionAsync` role check), the instance stays
  firing.
- `AcknowledgeAndResolve_AreAudited_WithTheTrueActor` — an Operator acking then resolving
  produces `alert.acknowledged` and `alert.resolved` audit rows with `actor = 't'` (the
  authenticated user, not a fixed string); the SHA-256 audit hash chain (Phase 4) covers
  these rows.
- `Save_IsRefusedForReadOnly_AtTheService` / `Delete_IsAdministratorOnly` — CRUD is
  Administrator/Operator; delete is Administrator-only.
- `AlertWebTests.Routes_RequireAuthentication` — `/alerts`, `/alerts/0`, `/alerts/templates`,
  `/alerts/history` all `302 → /login` unauthenticated.

## 5. SAST / SCA / secrets

- **No new dependency** — the alert engine uses only in-box BCL + the Phase 7 packages.
  `dotnet list package --vulnerable --include-transitive` → clean (14 projects).
- Every SQL statement in `Data/Alerts/` is parameterised; the group-by column is resolved
  against a fixed allow-list (`SqliteAlertWindowReader.GroupableColumns`), never
  interpolated from user input. `strftime` / `julianday` take bound parameters.
- Secrets: alert actions reuse the Phase 7 name-only model (`RuleAction.SecretName`); the
  `alert_action_queue.payload_json` carries the action, never a secret value — identical to
  `rule_action_queue`.
- `RuleActionValidator` (extracted from `RuleCompiler`, shared with `AlertCompiler`) applies
  the same SSRF / command-injection / traversal / SMTP-injection guards to an alert's
  actions as to a rule's — no drift (Phase 7 matrices still green: 421 → 469 → 547
  integration, no regression).

## New / changed threat-model entries (for the Phase 11 review)

- **B4 egress boundary** — alert-triggered actions share the Phase 7 executors, guards, and
  outbox pattern; add `alert_action_queue` / `AlertActionDispatchService` as a second
  drainer of the same executor registry.
- **B-alerts-1** — notification flooding: contained by dedup + global budget + re-notify
  interval (§1).
- **B-alerts-2** — information disclosure in alert content: instance listings scope-filtered,
  triggering events via `ScopedEventReader` (§2).
- **B-alerts-3** — stored XSS in alert fields: store-verbatim / encode-at-render, literal
  template substitution, plain-text email (§3).
- **B-alerts-4** — lifecycle authorization: ack/resolve Administrator/Operator only, audited
  with the true actor (§4).
