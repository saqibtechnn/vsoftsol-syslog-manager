# Phase 8 — UX gate (UX_STANDARDS.md §9)

Screens shipped: `/alerts` (open alerts + definitions), `/alerts/{id}` (editor),
`/alerts/templates`, `/alerts/history`; plus the "🔔 Alert me if this device goes silent"
action on the device health card and the open-alerts section in the notification centre.

## 1. Cold-eyes walkthrough — "set up an alert for when a core switch stops sending logs"

The device-silent alert is the headline differentiator, so it is offered as a one-click
action **on the device itself**, not buried in a generic builder (phase requirement).

| # | Click | Result |
|---|---|---|
| 1 | Dashboard → **Devices** (nav) | device list |
| 2 | the core switch's name | device detail + health card |
| 3 | **🔔 Alert me if this device goes silent** | toast: *"Created 'A monitored device goes silent'. It watches every monitored device — tune core-sw-1's heartbeat on its device page."* |

**3 clicks from the dashboard.** (≤ 5 — pass.) The alert is created **enabled**; it honours
each device's own `heartbeat_minutes` (already on the Phase 6 device form). Pressing the
button for a second device is idempotent — it reports the device is already covered rather
than creating a duplicate.

The generic builder path (for a threshold / distinct-count / absence alert) is
Dashboard → Alerts → **New alert** (or **Templates** → *Clone & edit*) → fill the form →
**Save** — 4–6 clicks depending on the template, with a live **"this would have fired N
times in the last 7 days"** preview so the operator tunes the threshold *before* saving
instead of discovering it by being spammed.

## 2. Empty-state check

| screen, zero data | state |
|---|---|
| `/alerts` open-alerts section | *"Nothing firing — Open alerts appear here, most severe first."* (🔔) |
| `/alerts` definitions section | *"No alerts yet — Alerts catch incident patterns … Start from a template."* (🔔) |
| `/alerts/history` | *"Nothing here yet — Fired alerts and their full lifecycle appear here."* (🔔) |
| `/alerts/templates` | always populated (8 cards) |
| notification centre, no open alerts | the "Open alerts" strip is simply absent |

No blank panels, no raw error text.

## 3. Error-path check

| input | response |
|---|---|
| save an alert with no name | *"The alert needs a name."* |
| window 0 / interval 0 / threshold 0 | *"The window must be between 1 second and 30 days."* / *"…interval must be at least 1 second."* / *"The threshold must be at least 1."* |
| distinct-count with no group-by field | *"A distinct-count alert must choose the field whose distinct values it counts."* |
| group by `message` | *"Group by a field with a bounded set of values (host, source IP, app…), not the message text — use a filter for message content."* |
| a webhook action with a bad URL | *"Action 1: webhook URL must be an absolute http or https URL."* |
| Read-Only / Auditor tries to acknowledge | *"You do not have permission to acknowledge or resolve alerts."* (refused at the service, not just hidden) |
| Operator tries to delete | *"Only an administrator can delete an alert."* |

Every rejection says how to fix it. Errors render in a `role="alert"` list above the save
button.

## 4. Keyboard-only check

The editor is standard `<input>` / `<select>` / `<textarea>` / `<button>` in DOM order;
`ConditionBuilder` and `RuleActionEditor` are the same components the Phase 6/7 keyboard
check covered. Tab reaches every field; Space toggles the enable switch and checkboxes;
Enter on **Save alert** submits. The one-click device-silent button is a plain `<button>` in
the health card's tab order. Ack / Resolve on the open-alerts table are `<button>`s reached
by Tab within the row.

## 5. Narrow-viewport check (1366×768)

`.ds-form__row` collapses to a column at ≤ 1100 px; `.ds-templategrid` goes single-column;
the alert tables scroll inside their own container (`.ds-table` inherits the design-system
`overflow-x` wrapper). No horizontal scroll on the page body. Verified against the rendered
markup + `ds.css` (no live browser on the dev VM — P4-2; axe-core + a 1366×768 screenshot
join the Phase 12 checklist).

## Templates

**8** shipped (phase asks for 6–8): Repeated login failures · Password-spray · Device
silent · No nightly backup · Interface flapping · Config-change burst · Firewall deny storm
· Critical hardware alarm. A clone starts **disabled** — the operator reviews it and turns
it on.

## Result

**5 / 5 — PASS.** Cold-eyes device-silent setup = 3 clicks. Live "would have fired" preview
present. 8 templates. axe-core / AT traversal / 1366×768 screenshot carried to Phase 12.
