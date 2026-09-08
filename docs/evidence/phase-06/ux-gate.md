# Phase 6 — UX gate (UX_STANDARDS.md §9)

Screens shipped: **Devices** (`/devices`), **Device detail / health** (`/devices/{id}`),
**Pending devices** (`/devices/pending`), **Device groups** (`/devices/groups`),
**Streams** (`/streams`), **Stream tester** (`/streams/tester`), **Discovery settings**
(`/settings/discovery`).

No browser on this host — the five points were run against the pre-rendered HTML and the
component source, the same substitution accepted since Phase 4 (P4-2, carried to Phase 12).

## 1. Cold-eyes walkthrough — required task

**"Approve a newly discovered device, name it, assign it to a group, and set its heartbeat
threshold."**

Starting point: the operator is on the dashboard. A switch has just started sending syslog
from an IP the system does not know.

| # | Click / action | Result |
|---|---|---|
| — | (glance at nav) | **Devices** entry shows a red badge **"1"** — discovery noticed without hunting (`NavMenu.razor`, `aria-label="1 pending devices"`) |
| 1 | Nav → **Devices ①** → the page's **"1 pending"** link (or Nav is directly on `/devices/pending` when a badge is shown) | Pending-devices queue: one row, **Name already pre-filled with the discovered hostname**, first-seen time, source IP |
| 2 | **Name** field — accept the pre-filled hostname or type a better one | (no click needed to accept; 1 to edit) |
| 3 | **Group** dropdown → pick "Core switches" | group set |
| 4 | **Heartbeat (min)** field → type `10` | heartbeat threshold set |
| 5 | **Approve** | toast "Approved "core-sw-3"."; the row disappears; the device is now under `/devices` with its health card |

**5 clicks (4 if the pre-filled name is kept), from the dashboard.** Meets the ≤ 5 gate.
Name / Vendor / Role / Heartbeat / Group are all on the one row — no drill-in, no modal,
no second save. Vendor and Role have `<datalist>` suggestions (Cisco / FortiGate / … ;
switch / router / firewall / server) so classification is a pick, not a guess.

Reject is the adjacent ghost button — same row, one click, no confirm dialog for a
discovered-but-unwanted source (the IP mapping is kept so it is not re-queued).

## 2. Empty-state check

| Screen / state | Message |
|---|---|
| Pending devices, none | "Nothing waiting — When the collector receives a message from a source it does not recognise, a device appears here for you to name and approve." |
| Devices, none registered | "No devices yet — Devices appear here once approved. Unknown sources wait in the pending queue." |
| Device detail, id not found | "Device not found — It may have been removed." |
| Device health card, no traffic yet | "no messages yet" in the last-seen slot; sparkline renders flat with an aria-label |
| Device groups, none | "No groups yet — Groups scope dashboards, filters, and (later) rules. Create one and add devices." |
| Streams, scoped user with none | "No streams visible to you." |
| Stream tester, message matches only the catch-all | each stream row shows "did not match the rule"; "All Messages" shows "catch-all — every message" |
| Stream rule won't compile | inline "The match rule has a problem." + the compiler's specific errors (same `ConditionCompiler` the ingest router uses) |
| Discovery settings | always populated (single seeded row) |

No blank panels, no raw stack traces (`_pending = 0` fallback in `NavMenu` so a nav query
error never breaks the shell).

## 3. Error-path check

- Approve with an empty name → toast "Give the device a name before approving it."
- Approve / reject as Operator or Read-Only → toast "Only an administrator can approve a
  discovered device." (refused at the service, `DeviceWebTests`).
- Save a stream with no name → "Give the stream a name."
- Save a stream whose regex rule uses a backreference / lookaround → "Pattern rejected: …
  Stream regex rules must be linear-time (no backreferences, lookarounds, or atomic
  groups)." — the same message the tester and the ingest router would give.
- Edit an out-of-scope stream by URL → "That stream is not available to you." (no
  existence oracle — `StreamScopeAndXssTests`).
- Discovery flood in progress → the pending queue stops growing at `max_pending_devices`;
  Settings shows the cap and (Phase 8) an alert fires. No UI lockup.

## 4. Keyboard-only check

- Pending-device row: `<input>` × 4 + `<select>` + two `<button>` — all native, all
  tab-reachable; datalist suggestions open on focus/type.
- Device detail edit form: native inputs; the health sparkline is `role="img"` with an
  `aria-label` summarising the trend (not keyboard-interactive by design — it is a readout).
- Streams: the visual `ConditionBuilder` (Phase 4 component — add/remove rule buttons are
  `<button>`, field/operator are `<select>`); the raw-JSON escape hatch is a `<details>`
  → `<textarea>` → Apply `<button>`.
- Stream tester: paste box is a `<textarea>`; "pick a recent message" is a `<select>`;
  results are a plain list.
- Nav badge is decorative text inside the existing `<a>` — the link is reached and
  activated by keyboard as before.

## 5. Narrow-viewport check (1366×768 and below)

`ds.css` Phase 6 block: `.ds-pending-row` and `.ds-condition-row` are `flex-wrap: wrap`, so
the inline fields stack on a narrow viewport instead of overflowing; `.ds-healthcard__grid`
and `.ds-checkgrid` are `repeat(auto-fit, minmax(…))`. The sparkline and any wide table
scroll inside their own container; the page body never scrolls horizontally. Live
screenshot carried to Phase 12 (P4-2 / P5-2).

## Visual condition editor (phase requirement)

The stream condition editor **is** the Phase 4 `ConditionBuilder` (`Streams.razor:68`).
A raw expression box is available **behind a `<details>` toggle** ("Raw rule (advanced)"),
never as the only option — exactly as the phase prompt requires. Round-trips both ways
(`OnRuleChanged` serialises the tree to the box; "Apply raw rule" parses the box back).

## Result

**PASS** — 5/5. Required task = 5 clicks (4 keeping the pre-filled name), 0 mandatory
characters beyond the heartbeat value. Pending-device badge in nav confirmed. Live
axe-core + AT traversal + screenshot carried to Phase 12 (no browser on this host).
