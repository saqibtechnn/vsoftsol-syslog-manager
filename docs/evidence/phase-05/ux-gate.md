# Phase 5 — UX gate (UX_STANDARDS.md §9)

Screens shipped: **Search** (`/search`), **Pattern tester** (`/search/pattern-tester`).
No browser on this host — the five points were run against the pre-rendered HTML and the
component source, the same substitution accepted for Phase 4 (P4-2, carried to Phase 12).

## 1. Cold-eyes walkthrough — required task

**"Find every authentication failure from one specific switch in the last 24 hours,
without typing a query."**

| # | Click | Result |
|---|---|---|
| 1 | Nav → **Search** | Search screen, time range already "Last 24 hours", all recent events listed |
| 2 | Sidebar → **Severity: Error** (checkbox) | grid narrows; query bar now shows `severity:error` |
| 3 | Sidebar → **Severity: Warning** (add) | query bar shows `(severity:error OR severity:warning)` |
| 4 | Sidebar → **Device: core-sw-1** (checkbox) | query bar shows `(severity:error OR severity:warning) device:core-sw-1` |
| 5 | (results already filtered — no submit needed; sidebar re-runs on change) | authentication-failure lines from that switch, last 24 h |

**5 clicks from the dashboard, zero characters typed.** Meets the ≤ 5 gate. The query bar
shows the exact query the sidebar built, so the syntax is taught by doing. Typing
`app:sshd` or `field.result:failed` would narrow further but is never required.

Context view (headline differentiator): from any row, **expand (▸) → "View context"** = 2
clicks to the ±50 surrounding messages from that host.

## 2. Empty-state check

| Screen / state | Message |
|---|---|
| Search, no data in range | "No results — No messages were stored in this time range. If a device should be sending, check the collector is receiving on the right port." + **Widen to Last 7 days** button |
| Search, query matched nothing | "Nothing matched in this time range. Check spelling, try a broader term, or widen the time range." + widen button |
| Search, scope/filter excluded everything | "Nothing matched this query in the selected time range. Try removing a filter, or widen the time range." |
| Search, first visit (no query) | "Search your logs — Pick filters on the left, or type a query. Every message the collector has stored is searchable here." |
| Query has a syntax error | the parser's plain-English message + the character position |
| Context view, no neighbours | "No surrounding messages — Nothing else from this host is within reach, or the neighbours are outside your visible scope." |
| Pattern tester, nothing extracted | "No fields yet — Nothing matched. Adjust the pattern, or check the sample line is representative." |
| Pattern tester, pattern won't compile | "Pattern won't compile" + the compiler error |
| Sidebar, no visible streams / devices | "No streams visible to you." / "No devices registered yet." |

No blank panels, no raw stack traces.

## 3. Error-path check

- Malformed query (`severity:banana`, unbalanced parens, `field:` with no value, unknown
  field, 10 KB input) → the query bar shows a specific message ("Invalid severity
  'banana' — use a name (error, warning, …) or 0-7") and the position; never an exception,
  never a full-table scan (`SearchInjectionTests`, `SearchQueryParserTests`).
- Save a search with no name → toast "Give the search a name before saving."
- Save a search with a duplicate name → toast "You already have a saved search with that name."
- Export with an invalid query → HTTP 400 with `{ error, position }` (`SearchWebTests`).
- Pattern tester: save with no name / no extracted fields → toast tells you what to fix first.

## 4. Keyboard-only check

- `/` focuses the global search (Phase 4 `app.js`, unchanged); the query bar is a plain
  `<input>`.
- Query-bar autocomplete: ↓/↑ move the highlight, Tab accepts, Esc dismisses.
- Sidebar filters are `<input type="checkbox">` / `<input type="search">` — all native,
  all tab-reachable.
- Results grid: sort buttons are `<button>`; the row expander is a `<button>` with
  `aria-expanded`; `<Virtualize>` keeps the DOM small.
- Context view is a `<Modal>` (Phase 4 — focus trap, Esc to close).
- Export menu and column chooser are `<details>` — keyboard-operable.

## 5. Narrow-viewport check (1366×768)

`ds.css` `@media (max-width: 1100px)`: the search body stacks (sidebar above results), the
pattern-tester two-column grid collapses to one. Wide content (results table, context
table, raw-message `<pre>`) scrolls inside its own container; the page body does not scroll
horizontally. Live screenshot carried to Phase 12 (P5-2).

## Result

**PASS** — 5/5. Required task = 5 clicks, 0 characters typed. Live axe-core + AT traversal
+ 1366×768 screenshot carried to Phase 12 (no browser on this host).
