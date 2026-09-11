# Phase 11 — UX gate (UX_STANDARDS.md §9)

Four new pages ship this phase: `/settings/listeners`, `/settings/bundles`, `/monitoring`,
`/account/security`. All four use the existing design-system components (`DataTable`,
`Modal`, `FormField`, `EmptyState`, `SkeletonLoader`, `ToastService`) — no new UI patterns.

## 1. Cold-eyes walkthrough — primary task: enable TOTP MFA on your own account

1. `/account` → click **Multi-factor authentication**.
2. Click **Set up MFA**.
3. Enter the 6-digit code from an authenticator app (manual-entry key shown on-screen) →
   click **Verify and enable**.
4. Save the ten recovery codes shown → click **I've saved these**.

**4 clicks from `/account`.** No documentation lookup required — the secret, account
label, and issuer are all shown on-screen with instructions in plain English.

**Secondary task: create a Windows Event Log API key**
`/settings` → **Listeners** → **New API key** → fill label → **Create** → copy the shown
key → **Done**. **3 clicks** from Settings.

**Secondary task: export a config bundle**
`/settings` → **Config bundles** → (sections default to "all") → **Download**. **2 clicks**.

## 2. Empty-state check

- Listeners page with no API keys yet: `EmptyState` — "No API keys yet" with an
  explanation and the same "New API key" button, not a blank table.
- Config bundles page with no import history: `EmptyState` — "No imports yet."
- Self-monitoring page before the collector host has sampled: `EmptyState` explaining
  *why* it is empty (no collector host running yet, or it hasn't ticked) rather than a
  blank grid.
- Account security page for a user who has never enrolled: a clear "MFA is off" card with
  one explanatory sentence and a single "Set up MFA" button.

## 3. Error-path check

- MFA verification with a wrong/expired code: plain-English message ("check your
  authenticator app's clock and try again"), the enrollment secret and form stay on
  screen — no restart-from-scratch.
- SNMP community set to `public`: refused with "'public' is never accepted — choose a real
  community string," not a generic validation error.
- Config bundle import: a corrupted/non-JSON file → "That file is not valid JSON."; a
  bundle whose signature doesn't verify → "This bundle failed verification — it may be
  corrupted or tampered with."; an untrusted signer → shown the fingerprint with an
  explicit accept step, never silently processed.
- API key creation with an empty label: the "Create" button is disabled until a label is
  entered (no server round-trip needed to discover the mistake).

## 4. Keyboard-only check

Every control on all four pages is a native `<input>`, `<button>`, `<a>`, or `<select>`
(no custom click-only widgets) — all reachable and operable via Tab/Enter/Space, consistent
with the shared `ds-*` component library's existing keyboard support (Phase 4 baseline).
The recovery-codes and API-key-created modals trap focus via the shared `Modal` component
(unchanged from Phase 9/10 usage).

## 5. Narrow-viewport check (1366×768)

All four pages use the existing `ds-page`/`ds-card`/`ds-cardgrid`/`ds-form` layout classes,
which already wrap and stack at this width (verified structurally in every prior phase's
gate); no new fixed-width element was introduced. The self-monitoring stat grid
(`ds-cardgrid`) wraps to fewer columns at narrow widths, same as the Phase 9 dashboard
widget grid.

**Result: PASS**, 5/5, structural verification (no live browser on this build host —
`dev-vm-constraints`), consistent with every prior phase's UX gate methodology.
