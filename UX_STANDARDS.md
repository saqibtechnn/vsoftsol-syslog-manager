# UX_STANDARDS.md — VSoftSol Syslog Manager

Binding requirements for every screen in the product. Referenced by `CLAUDE.md` and
enforced by the UX gate in each phase. These are written as testable rules because
"make it user friendly" is not something anyone can verify.

**The user we are designing for:** a network or systems administrator, not a data
engineer. They know switches, VLANs, and firewalls. They do not know GROK syntax,
Lucene, or index lifecycle management. Every feature must be usable without learning
a query language or reading documentation first.

---

## 1. The ten-minute rule

From finished installer to seeing live messages from a real device must take under ten
minutes, with **no configuration file editing and no documentation lookup**. Every
design decision that adds a step to that path must be justified or reversed.

## 2. Setup and onboarding

- **First-run wizard**, unskippable, maximum 5 steps: admin password → listener ports →
  data directory → retention preset (Small / Medium / Large, with the disk estimate for
  each shown inline) → optional vendor config bundle import. Nothing else.
- After the wizard, land on a **"Waiting for messages" page** that shows the exact
  commands to configure a device to send here, with the server's own IP and port already
  filled in, in copy-ready blocks for Cisco IOS, ASA, FortiGate, Palo Alto, Juniper,
  MikroTik, and Linux rsyslog. This page auto-advances to the dashboard the moment the
  first message arrives.
- **Sensible defaults everywhere.** The product must be fully functional with zero
  configuration beyond the wizard. Ship with working streams, working default
  dashboards, and retention already set.
- No blank slates. Every list view that could be empty on day one ships with either
  seeded content or a single obvious primary action.

## 3. Progressive disclosure

- Every configuration screen has a **Basic** view showing the 3-5 fields that matter and
  an **Advanced** section, collapsed by default, holding everything else.
- Rules, alerts, and stream conditions are built with a **visual condition builder**
  (field / operator / value rows with AND-OR grouping) as the default. A raw expression
  editor is available behind a toggle for power users — never as the only option.
- Search has a **filter sidebar** (device, severity, stream, time) that composes the
  query for the user. The query bar shows what the sidebar produced, so users learn the
  syntax by seeing it. Typing in the bar is never required.

## 4. Never make the user guess

- Every input has inline validation with a message saying **what is wrong and how to fix
  it** — "Port 514 is already in use by another listener" not "Invalid value".
- Every destructive action names what will be lost and requires typed confirmation only
  when the loss is unrecoverable.
- Every configuration screen with real-world consequences has a **test button**:
  test SMTP, test webhook, test rule against a sample message, test extractor pattern,
  test stream match, test device connectivity. Test never has a side effect.
- Field-level help via a hover icon, written in one sentence in plain English.
  Nothing that requires knowing the RFC.
- **Empty states teach.** Zero devices, zero rules, zero alerts each explain what the
  thing is, why it matters, and offer one button to create the first one.

## 5. Feedback and state

- Any action taking over 400 ms shows a progress indicator. Any action over 5 s runs in
  the background with a notification on completion — the user is never blocked.
- Every save produces a visible confirmation. Optimistic UI is not used for
  configuration changes.
- Errors surface a plain-English summary plus an expandable technical detail block with
  a correlation ID that matches the internal log.
- Unsaved changes prompt before navigation.

## 6. Layout and consistency

- One navigation structure, consistent across every page: Dashboards · Search · Devices ·
  Streams · Rules · Alerts · Reports · Settings.
- Global time-range picker in a fixed position, and it means the same thing everywhere.
- One table component, one form component, one modal component. If a page needs a
  different pattern, that is a design smell — raise it rather than forking the component.
- Keyboard: `/` focuses search, `Esc` closes modals, `Enter` submits forms,
  arrow keys navigate the results grid, `?` opens a shortcut list.
- Severity colour coding is identical on every screen and is **never the only carrier of
  meaning** — always paired with a label or icon.

## 7. Performance is a UX requirement

- First contentful paint under 1.5 s on the local network.
- No screen blocks on a query longer than 3 s; show partial results or skeletons.
- The results grid is virtualized; scrolling 50,000 rows must stay smooth.
- Auto-refresh never steals scroll position or closes an open row.

## 8. Accessibility floor

- All interactive elements reachable and operable by keyboard, with a visible focus ring.
- Colour contrast meets WCAG 2.1 AA.
- Form inputs have associated labels; icon-only buttons have accessible names.
- Works at 1366×768 (the resolution of the average server-room laptop) without
  horizontal scrolling.

## 9. Verification — the UX gate

Every phase that ships a screen must pass this before its commit:

1. **Cold-eyes walkthrough**: perform the phase's primary task as a first-time user with
   no documentation. Write the steps taken and the click count into PROGRESS.md.
   If the task takes more than 5 clicks from the dashboard, redesign it.
2. **Empty-state check**: load every new screen with zero data. No exceptions, no blank
   panels, no raw error text.
3. **Error-path check**: submit every new form with invalid, empty, and hostile input.
   Every rejection must say how to fix it.
4. **Keyboard-only check**: complete the primary task without touching the mouse.
5. **Narrow-viewport check**: 1366×768, no horizontal scroll.

Record the result of all five in PROGRESS.md under the phase entry.
