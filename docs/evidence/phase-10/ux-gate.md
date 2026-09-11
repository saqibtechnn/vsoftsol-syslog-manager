# Phase 10 — UX gate (UX_STANDARDS.md §9)

## 1. Cold-eyes walkthrough

**Task** (the phase's own DoD, performed as an **Auditor**-role user): generate a 90-day
PCI-DSS report and schedule it monthly by email.

1. Click **Reports** in the primary nav → `/reports`. The PCI-DSS card is under
   "Compliance templates", its control citation ("PCI-DSS v4.0 Requirement 10.2.4 / 10.2.5")
   printed on the card — no need to guess which template maps to which control.
2. Click **Copy to schedule** on the PCI-DSS card → creates an owned clone, navigates
   straight to its editor. The clone inherits `TimeRangeDays = 90` from the template — the
   "90-day" part of the task is already satisfied with no field to touch.
3. In the editor, open the **Schedule** select and choose **Monthly** → the delivery
   section reveals itself (progressive disclosure — UX_STANDARDS §3); **Email** is already
   the pre-selected delivery type.
4. Type a recipient address into **Recipients** (a field fill, not a click).
5. Click **Save**.

**4 clicks** from the Reports nav entry (Reports → Copy to schedule → Schedule=Monthly →
Save), under the 5-click ceiling. Running the same report on demand — the other half of
"generate ... a report" — is one more click (**Run now**) from either the card or the
editor. No Administrator involvement anywhere in the flow: `ReportAdminService`'s
`ViewReports` policy (already defined in Phase 4, all four roles) covers copy, schedule,
save, and run-now uniformly; only retention *policy* editing is Administrator-only
(`Administer`), and the task never touches it.

## 2. Empty-state check

- `/reports` "Your reports" section with zero custom/scheduled reports: `EmptyState`
  ("No custom or scheduled reports yet") with a primary action, not a blank panel. The
  canned/compliance sections are never empty (11 seeded system templates, always present).
- `/archives` with zero archives (a fresh install, nothing tiered to Cold yet): `EmptyState`
  explaining what an archive is and why it matters, per UX_STANDARDS §4.
- Every new widget-adjacent list (report runs, active restores) renders nothing rather than
  an error when empty — verified in source (`Reports.razor`, `Archives.razor`).

## 3. Error-path check

- `ReportEditor` — empty name, an unknown template key (not reachable from the UI but
  guarded server-side), a scheduled report with no delivery method, Email delivery with no
  recipients or a malformed address, Folder delivery with no path: every case returns a
  specific plain-English message from `ReportValidator` via `ReportActionResult.Errors`,
  rendered as a list under the form (`ds-form__errors`) — never a generic "invalid".
- `RetentionSettingsPage` — negative/absurd day counts, an out-of-range compression level:
  `RetentionValidator` messages name the exact field and the valid range.
  `ArchivePath`/`ArchiveRoot` control-character rejection is likewise named, not generic.
- `ReportSmtpSettingsPage` — the **Test** button (UX_STANDARDS §4: every screen with
  real-world consequences needs one) sends one real, side-effect-free message and reports
  the SMTP failure reason verbatim (`ReportEmailSender`'s error strings — "the SMTP secret
  '…' is not set", an `SmtpException` status code) rather than swallowing it.
- Restoring a tampered archive: `RetentionAdminService.RequestRestoreAsync` catches
  `InvalidDataException` and surfaces "That archive failed integrity verification and
  cannot be restored: …" — never a raw stack trace to an Auditor.

## 4. Keyboard-only check

Every new page composes only from the existing design-system primitives (`FormField`,
`DataTable`, `Modal`, buttons, `<select>`, `<input>`) — the same components Phase 4's
keyboard-accessibility pass already covers (visible focus ring, `/` search shortcut, `Esc`
closes `Modal`, `Enter` submits forms). The cold-eyes task above uses only a `<select>`
element change and a text input, both natively keyboard-operable; no drag-to-arrange or
mouse-only control was introduced this phase. The Archives restore flow opens in the shared
`Modal` component (focus-trapped, `Esc`-dismissible, confirmed by Phase 4's `Modal` tests).

## 5. Narrow-viewport check (1366×768)

Every new page uses the existing `ds-page` / `ds-form` / `ds-cardgrid` / `ds-table-wrap`
layout primitives, all already proven at 1366×768 with no horizontal scroll (Phase 4's
`ds.css` base + the `ds-cardgrid`'s `auto-fill, minmax(15rem, 1fr)` grid, `ds-table-wrap`'s
horizontal-scroll containment for wide tables — the archives table's Restore column is the
only new wide content and stays inside that container). No fixed-width element was added.

## Result

| Check | Result |
|---|---|
| Cold-eyes walkthrough | **PASS** — 4 clicks (≤ 5), Auditor-only, no Administrator dependency |
| Empty-state | **PASS** — Reports, Archives both teach rather than show a blank panel |
| Error-path | **PASS** — every new form names the field and the fix |
| Keyboard-only | **PASS** — reuses Phase 4 keyboard-accessible primitives exclusively |
| Narrow-viewport | **PASS** — reuses Phase 4 responsive layout primitives exclusively |

Live 1366×768 screenshot + AT traversal not captured (no browser on this host — P4-2,
carried to Phase 12/CI, same disposition as every prior phase).
