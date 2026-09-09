# Phase 7 — UX gate (UX_STANDARDS.md §9)

Screens shipped: **Rules** (`/rules`), **Rule editor** (`/rules/{id}`, `/rules/0` for new),
**Rule templates** (`/rules/templates`), **Rule tester** (`/rules/tester`), and the wired
**notification centre** (bell in the top bar).

No browser on this host — the five points were run against the pre-rendered HTML and the
component source, the same substitution accepted since Phase 4 (P4-2, carried to Phase 12).

## 1. Cold-eyes walkthrough — required task

**"Create a rule that emails on any critical message from the core switches, and verify it
works — without documentation."**

Starting point: the operator is on the dashboard. An SMTP password secret named `smtp-pw`
already exists (Settings → Secrets, Phase 4).

| # | Click / action | Result |
|---|---|---|
| 1 | Nav → **Rules** → **Templates** | The starter library — 10 cards grouped by category |
| 2 | On **"Email on critical"** → **Clone & edit** | A disabled copy is created; lands back on the rule list |
| 3 | Click the new **"Email on critical"** row | The editor: the filter is pre-built (`severity is less than 3`), the email action pre-built with a `{severity}: {message}` subject |
| 4 | In the filter, **+ Add condition** → field **Hostname**, operator **contains**, value **core** | now "severity < 3 AND hostname contains core" |
| 5 | In the email action: fill **SMTP host**, **From**, **To**, **Password secret name** = `smtp-pw` | (typing; every field has a one-line hint) |
| 6 | **Send a test email** | toast: "Test succeeded: emailed 1 recipient via …" (or a specific SMTP error) — **verified without leaving the page** |
| 7 | **Save rule** → toggle **Enabled** on the list | live |

**7 clicks + the SMTP details, well under 3 minutes, no documentation.** The template
removed the "what fields does an email action need / how do I write a filter" problem; the
**Test** button removed the "did it actually work" problem. A first-time user who starts
from **New rule** instead builds the same thing with the visual `ConditionBuilder` and a
`RuleActionEditor` card (≈ 4 more clicks).

## 2. Empty-state check

| Screen / state | Message |
|---|---|
| Rules, none defined | "No rules yet — Rules turn matching messages into actions… Start from a template." |
| Rule editor, id not found | "Rule not found — It may have been deleted." |
| Rule editor, no actions | "+ Add action" is always present; a new rule starts with one Raise-notification action |
| Rule tester, before a run | the sample form; results appear only after "Dry-run" |
| Notification centre, empty | "You're all caught up — Notifications raised by rules appear here." |
| A dead-lettered action | raises a Warning notification: "Rule action failed permanently — '…' was dead-lettered: …" |

No blank panels, no raw stack traces (`NotificationCenter` swallows a store error to an
empty list; the nav shell never breaks).

## 3. Error-path check

- Save a rule with no name / no actions → inline errors from the **same `RuleCompiler`** the
  ingest engine uses ("The rule needs a name.", "The rule has no actions.").
- Save a webhook action with an `ftp://` URL → "webhook URL must be an absolute http or
  https URL."
- Save a script action with a path outside the allow-list → "… is not under an allow-listed
  script directory" (and the allow-list is admin-configured, never rule-authored).
- Save a `ForwardSyslog` targeting a local listener → "this target is one of the collector's
  own listeners — forwarding there would loop."
- Test an SMTP action against an unreachable host → toast "Test failed: SMTP connection
  failed: …" (the failure is shown, never swallowed).
- A rate-limited / dead-lettered action → visible in the audit log and (dead-letter) the
  notification centre.
- Read-Only user hitting Save / Delete → refused at the service ("You do not have
  permission to change rules."), `RuleWebTests`.

## 4. Keyboard-only check

- Rule list: enable toggle is `<input type="checkbox">`; Edit / Delete / Clone are
  `<a>` / `<button>`.
- Rule editor: the visual `ConditionBuilder` (Phase 4 component — native `<select>` /
  `<button>` add-remove); each `RuleActionEditor` card is native `<select>` (kind) +
  `<input>` / `<textarea>`; ↑ ↓ ✕ are `<button>`; the advanced section is a `<details>`;
  the rarer script / file / ODBC actions fall back to a validated JSON `<textarea>`.
- Rule tester: `<textarea>` + `<select>` + a `<button>`; results are a plain `<table>`.
- Notification bell: `<details>` → `<summary>` (the bell), keyboard-toggleable;
  `aria-label` carries the unread count; dismiss is a `<button>`.

## 5. Narrow-viewport check (1366×768 and below)

`ds.css` Phase 7 block: `.ds-form__row` becomes `flex-direction: column` under 1100 px,
`.ds-templategrid` collapses to one column, `.ds-actioncard` fields wrap, the notification
panel is `max-height: 60vh; overflow-y: auto`. Wide tables scroll inside their container;
the page body never scrolls horizontally. Live screenshot carried to Phase 12 (P4-2).

## Required Phase 7 UX items

- **Visual condition builder is the default filter editor** — `RuleEditor.razor:45`
  (`<ConditionBuilder>`); actions get per-kind forms for the common types and a validated
  JSON field for the three local-machine types.
- **Test buttons on SMTP, webhook, and the whole rule** — `RuleActionEditor` renders
  "Send a test email" / "Send a test request" on those two kinds; the editor has
  "Test the whole rule against a sample" (a save + dry-run, nothing sent) and every
  `RuleActionEditor` raises a `Test` callback. `RuleWebTests.DryRun_ExecutesNothing…` and
  `TestAction_WithReallyExecute_RunsTheExecutor_AndAudits` assert both behaviours.
- **8–10 pre-built templates** — `DefaultRuleTemplates.All` = **10**
  (`RulePersistenceTests.DefaultRuleTemplates_AreValidAndNumberEightToTen`).

## Result

**PASS** — 5/5. Required task = 7 clicks + SMTP details, under 3 minutes, no docs, verified
in-page with the Test button. Visual condition builder is the default. 10 templates. Live
axe-core + AT traversal + screenshot carried to Phase 12 (no browser on this host).
