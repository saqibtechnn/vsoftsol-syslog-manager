# Phase 12 — UX gate (UX_STANDARDS.md §9)

Five new surfaces ship this phase: the first-run wizard (`/setup`), the "Waiting for
messages" landing page, `/help`, `/about`, and the dashboard's Getting Started checklist.
The wizard and waiting page are the product's literal first impression — treated
accordingly.

No live, untrained human tester was available in this environment (see
`known-issues.md`); this is a documented cold-eyes walkthrough plus the automated
`FirstRunWizardTests` end-to-end exercise of every step's mechanics, not a substitute for
the real observed session PHASE_12_RELEASE.md calls for. That live session remains an open
item, carried explicitly rather than asserted as done.

## 1. Cold-eyes walkthrough — primary task: install and reach the dashboard

1. Run the MSI → **Next** through the license/install-location dialogs (WixUI_Minimal,
   standard) → **Install**.
2. Browser opens to `https://<server>:5443` → lands directly on **Set up your
   administrator password** (step 1 of 5) — no login screen shown first, since there is no
   working credential yet.
3. Step 1: enter password twice → **Continue**.
4. Step 2 (**Listener ports**): defaults already filled in (514/514/5443) → **Continue**
   (zero typing needed unless a port actually conflicts).
5. Step 3 (**Data directory**): shown, read-only → **Continue**.
6. Step 4 (**Retention**): three cards, each showing its own projected disk usage inline,
   Medium pre-selected → **Continue** (zero clicks needed beyond Continue if the default is
   acceptable).
7. Step 5 (**Import a vendor configuration**): optional file field, left empty → **Finish
   setup**.
8. Lands on **Waiting for messages** — pinned vendor picker, first pinned vendor's commands
   already shown with this server's real address and port substituted in.
9. Configure one device with the shown commands → the moment its first message arrives, the
   page auto-advances itself to the Network Overview dashboard — no click needed.

**Zero documentation lookups, zero configuration-file edits, five wizard screens plus one
waiting screen** — consistent with the ≤ 10-minute, unaided pass condition, modulo the
device's own configuration time (outside this product's control).

**Secondary task: open the keyboard shortcut list.** Press `?` anywhere outside a text
field → lands on `/help` with the shortcut table and a live search-as-you-type filter.
**Zero clicks** (one keypress).

**Secondary task: check product version.** `/about` (reachable by direct navigation; not
cluttering the primary nav) shows version, build date, licence line, vendor, and support
contact on one screen. **0 clicks** once navigated there.

## 2. Empty-state check

- A dashboard with none of "first device / first rule / first alert" done yet: the Getting
  Started card explains each of the three remaining steps with a direct link to where to do
  it (`/rules`, `/alerts`) — never a blank space with no next action.
- The waiting page's device picker with a search term matching nothing: the grid simply
  shows no chips (a search with zero results is self-evident here; no separate empty-state
  copy was added, consistent with how the equivalent search-filter pattern behaves
  elsewhere in the product for a live, incremental filter rather than a submitted query).
- Help page shortcut search with no matches: rows hide via the same filter, leaving the page
  header and "Need more?" section visible — never a fully blank page.

## 3. Error-path check

- Wizard step 1, passwords that don't match: inline error, same step, both fields retained.
- Wizard step 1, password below the minimum length: inline error stating the exact minimum.
- Wizard step 2, two ports set to the same value: inline error ("The three ports must all
  be different from each other"), same step, values retained.
- Wizard step 5, an invalid or corrupted config bundle file: the finish action still
  completes setup (the step is explicitly optional — a bad file must never block finishing)
  rather than failing the whole wizard on an optional, secondary action.
- Attempting to reach `/setup` again after setup has already completed: silently and
  immediately redirected to the dashboard, not an error page and not a re-run of the wizard.

## 4. Keyboard-only check

Every wizard step is a plain HTML form (`<input>`, `<button type=radio>` via
`InputRadioGroup`/`InputRadio`, a native `<input type=file>`) — fully Tab/Enter operable,
no custom click-only control anywhere in the flow. The waiting page's vendor picker uses
plain `<button>` chips, individually focusable and activatable with Enter/Space. The help
page's search input is a native `<input type=search>`.

## 5. Narrow-viewport check

The wizard and waiting page use the same `ds-page`/`ds-card`/`ds-form`/`ds-field` layout
primitives as every other page in the product (new `ds-wizard`/`ds-waiting` classes are
additive layout wrappers around them, not a parallel component system), which already carry
the product's existing responsive behaviour. The wizard card and waiting-page content both
cap at a readable max-width and reflow to single-column below it, consistent with the
existing `ds-bare__card` (login) pattern this phase's `WizardLayout` deliberately mirrors.
