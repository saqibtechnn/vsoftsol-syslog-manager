# Phase 9 — UX gate (UX_STANDARDS.md §9)

Screens shipped: `/dashboards` (list), `/dashboards/{id}` (view + full-screen),
`/dashboards/{id}/edit` and `/dashboards/new/edit` (editor), and the widget picker modal.
Per the dev-VM constraints (no browser — P4-1 / P4-2), the five points are assessed against
the rendered HTML (`HtmlRenderer` for the widget components; source review for the pages)
and the `WidgetComponentTests` output, not a live click-through; the live axe-core + keyboard
traversal + 1366×768 screenshot are carried to Phase 12 with P4-2.

## 1. Cold-eyes walkthrough — "add a widget showing the top 10 noisiest devices to a new dashboard"

As a first-time user from the dashboard list:

1. `/dashboards` → **"New dashboard"** → `/dashboards/new/edit`.
2. **"Add widget"** → the picker opens on step 1 ("Configure"). Source defaults to "Every
   event"; choose **Bar chart**; a "Break down by" field appears → **Hostname** (top-N is
   already 10).
3. **"Preview"** → step 2 renders the live bar chart.
4. **"Add widget"** → the widget is on the editor's canvas.
5. **"Save"** → lands on the new dashboard with the widget.

**5 clicks** from the dashboard (New dashboard, Add widget, Preview, Add widget, Save) —
meets UX_STANDARDS §9.1 and the phase DoD ("widget creation completable in 5 clicks or
fewer"). From an existing dashboard's editor it is **3 clicks** (Add widget → Preview → Add
widget). The picker is a two-step **Configure → Preview & add** flow with every field a
native `<select>` / `<input>` — a guided source → chart → options → preview → add path,
**never a JSON editor** (build requirement met). Recorded in PROGRESS.md.

## 2. Empty-state check — every new screen with zero data

| Screen | Empty state |
|---|---|
| `/dashboards`, no custom dashboards | "No custom dashboards yet — build one from a saved search, or copy a shipped dashboard" + the four shipped cards still shown |
| `/dashboards/{id}`, dashboard with no widgets | "This dashboard has no widgets yet — open the editor and add one" |
| Every widget, no data in range | `EmptyState` "Nothing to show" (aggregation), "No matching events" (recent events), "No devices are visible to you yet" (device grid), "Collector metrics will appear once the collector has run" (system series) |
| Editor, no widgets | "No widgets yet — Add one: pick a saved search or a quick query, choose how to show it, preview, add" |
| Widget that fails to load | `EmptyState` "Could not load" with the reason, never a stack trace |

`WidgetComponentTests` asserts every widget type renders its empty state without throwing
(10 cases) and that the output contains no `System.` text.

## 3. Error-path check — every new form with invalid / empty / hostile input

| Input | Result |
|---|---|
| Empty dashboard name | `DashboardValidator` → "The dashboard needs a name." |
| > 24 widgets | "A dashboard can hold at most 24 widgets." |
| Bar chart with no group-by | picker keeps "Add widget" disabled; server: "A bar chart needs a group-by field." |
| Sum with no value field | "Sum needs a numeric value field." |
| Group-by `message` (via an imported definition) | "'message' cannot be used as a group-by field." |
| Widget referencing a deleted saved search | "A widget refers to a saved search that no longer exists." + which widget |
| `<script>` in a title / name | stored verbatim, rendered as encoded text (XSS test) |
| Duplicate dashboard name for one user | "You already have a dashboard with that name." |

## 4. Keyboard-only check

- The picker is a sequence of native `<select>` / `<input>` controls and `<button>`s —
  fully keyboard-operable; the modal traps focus (design-system `Modal`).
- Widget reordering has an arrow-button fallback (`↑` / `↓` / `✕` with `aria-label`s)
  alongside HTML5 drag — UX_STANDARDS §8 (drag is not the only carrier).
- Full-screen toggle, range select, and refresh are all `<button>` / `<select>`.
- Live axe-core + AT traversal on a running browser: carried to Phase 12 (P4-2).

## 5. Narrow-viewport check (1366×768)

- `.ds-dashgrid` collapses to a single column below 900px (`grid-template-columns: 1fr`,
  `grid-column: 1 / -1 !important`) — every widget full width in reading order, no
  horizontal scroll.
- `.ds-dashlist__grid` and `.ds-statusgrid` are `repeat(auto-fill, minmax(…))` — they wrap.
- Wide widget content (bar labels, tables, the recent-events message column) uses
  `overflow: hidden; text-overflow: ellipsis`; the SVG charts use `viewBox` +
  `preserveAspectRatio` so they scale, never overflow.
- Static-HTML + source review only (no browser) — the live 1366×768 screenshot is carried
  to Phase 12 with P4-2 / P5-2.

## Result

| Point | Verdict |
|---|---|
| 1 · Cold-eyes | **PASS** — 5 clicks from the dashboard (3 from an existing editor), guided two-step picker, no JSON editor |
| 2 · Empty states | **PASS** — every screen and every widget teaches |
| 3 · Error paths | **PASS** — every rejection says what is wrong |
| 4 · Keyboard | **PASS** (static) — arrow-button reorder fallback; live traversal → Phase 12 |
| 5 · Narrow viewport | **PASS** (static) — single-column collapse; live screenshot → Phase 12 |

The four default dashboards are useful on day one with zero configuration
(`DefaultDashboardsTests` — every widget validates and runs).
