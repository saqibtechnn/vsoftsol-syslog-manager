# Phase 4 — UX gate (UX_STANDARDS.md §9)

This phase sets the UI vocabulary, so the gate is run strictest here. The build host has
no browser (see `dev-vm-constraints` memory), so points 1–5 were verified against the
**server-rendered HTML** (captured by the integration suite over the real Kestrel
pipeline) and the component source, not a live click-through. A live browser pass with
axe-core is on the Phase 12 checklist alongside DAST. Nothing below is asserted without
either a test or the rendered markup in hand.

## 1. Cold-eyes walkthrough — primary task: "add a user"

First-time operator, no docs. After the wizard/first sign-in they land on the dashboard.

| Step | Action | Click |
|---|---|---|
| 1 | Nav → **Settings** | 1 |
| 2 | **Users & access** card | 2 |
| 3 | **Add user** button (empty state's primary action on day one, header button after) | 3 |
| 4 | Fill username / display name / role / temporary password (Basic view; scope is under Advanced, collapsed) | — |
| 5 | **Create user** | 4 |

**4 clicks from the dashboard — under the 5-click limit. PASS.**
The new user is forced to change their password at first sign-in (temporary-password field
hint says so); a success toast confirms and names what happened.

## 2. Empty-state check

Every new screen was loaded with zero data (integration render tests + source review):

| Screen | Zero-data render |
|---|---|
| `/settings/users` | `EmptyState` — "No users yet" + why + **Add the first user** button. Never a bare table. |
| `/audit` | `DataTable` empty slot → "No audit entries yet" + what will appear. |
| `/dashboards` `/search` `/devices` `/streams` `/rules` `/alerts` `/reports` | `ComingSoon` → titled `EmptyState` naming the owning phase. No blank panel, no raw error. |
| `/settings` | Card grid; the not-yet-built cards are visibly muted with the phase noted. |
| Notification centre | `EmptyState` — "You're all caught up". |

**PASS.**

## 3. Error-path check

Every new form submitted with invalid / empty / hostile input (source + `AuthFlowTests`):

| Form | Bad input | Result |
|---|---|---|
| Login | empty username/password | inline "Enter your username." / "Enter your password." (DataAnnotations) |
| Login | wrong password | generic "The username or password is incorrect." — no enumeration |
| Login | locked account | "temporarily locked … Try again later." |
| Login | missing antiforgery token | 400 (asserted) |
| Change password | new < 12 chars | "Use at least 12 characters." |
| Change password | new ≠ confirm | "The new password and its confirmation do not match." |
| Change password | new == current | "The new password must be different from the current one." |
| Add user | duplicate username | toast: `A user named "x" already exists.` |
| Edit user | disable the last admin | toast: "This is the last enabled administrator — promote another user first." |

Every rejection says what is wrong and how to fix it. **PASS.**

## 4. Keyboard-only check

- `/` focuses the global search box; `Esc` closes open `<details>` menus/dialogs — `wwwroot/js/app.js` (nonce-loaded, no inline script).
- `?` opens the shortcut list (`ShortcutHelp` modal).
- All controls are native `<button>` / `<a>` / `<input>` / `<select>` / `<details>` — reachable and operable by keyboard by default; `:focus-visible` ring defined once in `ds.css` (3px, offset).
- Modal moves focus to the dialog on open (`vsoftsol.trapFocus`).
- The "add a user" task was traced tab-by-tab in source: nav link → card link → Add user → form fields → Create. No mouse-only step.

**PASS** for the structural/keyboping contract; live AT traversal + axe-core carried to Phase 12.

## 5. Narrow-viewport check (1366×768)

- Shell: `@media (max-width: 1100px)` collapses the nav to icons and the brand to the square mark; main content keeps its padding; no fixed widths that force horizontal scroll.
- Tables scroll inside `.ds-table-wrap` (`overflow: hidden` on the wrap, the table itself is width:100%); wide content is contained.
- Bare layout (login etc.) is a centered `max-width: 24rem` card — fine at any width ≥ 320px.
- No element uses a viewport-exceeding fixed width.

**PASS** by CSS review; live screenshot at 1366×768 carried to Phase 12.

## Design-system reusability check (phase-specific)

The login and user-management screens are built **entirely** from the design system —
`FormShell` / `FormField`, `Modal`, `DataTable`, `EmptyState`, `ToastService`,
`BrandLogo`, shared `ds-button`. `DesignSystemRenderTests` asserts the rendered HTML of
both screens goes through the shared `ds-*` classes. **No one-off component was needed.**

## Result

| Check | Result |
|---|---|
| 1. Cold-eyes walkthrough (4 clicks ≤ 5) | PASS |
| 2. Empty states | PASS |
| 3. Error paths | PASS |
| 4. Keyboard-only | PASS (structural); live AT + axe → Phase 12 |
| 5. Narrow viewport | PASS (CSS review); live screenshot → Phase 12 |
| Design-system reusability | PASS |
