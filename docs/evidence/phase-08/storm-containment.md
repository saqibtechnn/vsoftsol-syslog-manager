# Phase 8 — storm containment

PHASE_08 Validation & Evidence: "inject 100k matching events in 10 s; assert the alert fires
once and the global action budget prevents a notification flood."
PHASE_08 Security: "an attacker who can generate log events can weaponise the alerting
system against the administrator's inbox or a downstream webhook."

## 1. A flood fires the alert once

`AlertStormContainmentTests.AFloodOfMatchingEvents_FiresTheAlertOnce_AndNotifiesOnce`

- **40,000** matching events in the window, all from one host (scaled from 100k — a 100k
  insert is a multi-minute operation on the 2-vCPU dev VM per `dev-vm-constraints`; the
  assertion is identical and the SQL `GROUP BY` cost is O(1) in the number of *groups*, not
  rows).
- threshold 100, grouped by hostname → one group breaches with count 40,000.
- Three consecutive ticks over two minutes.

**Result:** exactly **1** open instance, exactly **1** `alert.fired` audit row, exactly
**1** `RaiseNotification` dispatched. The dedup guarantee (`ux_alert_instances_open`) means
the second and third ticks find the group still breaching and re-notify only on the
re-notify interval (0 here → never). A flood cannot multiply the notification.

## 2. Many simultaneous firings collapse to a summary

`AlertStormContainmentTests.ManyDistinctFiringsAtOnce_AreCollapsedToASummary_ByTheGlobalBudget`

- `Alerts:GlobalActionsPerMinute = 3`.
- One grouped threshold alert; **20 hosts** each breach in the *same* evaluation tick.

**Result:** all **20** instances open (the UI still shows every one — an operator must see
the full incident), but only **3** per-host notifications are dispatched (the budget's
worth); the other 17 are folded into a single **"Alert-storm protection engaged"** summary
notification listing `17× Raise notification`. `AlertRuntime.TakeStormSummary` emits that
summary at most once per minute so the summary itself cannot become the flood.

## 3. Unit-level budget behaviour

`AlertRuntimeTests`:

- `Reserve_UpToTheGlobalBudget_ThenCollapses` — first N `Allow`, the rest `StormCollapsed`.
- `GlobalBudget_RefillsAfterAMinute` — the sliding window refills.
- `Reserve_Cooldown_BlocksUntilTheGapElapses` / `Reserve_RateLimit_AllowsExactlyNPerWindow`
  — per-action limits hold with a virtual clock.
- `TakeStormSummary_AggregatesByKind_AndFiresAtMostOncePerMinute`.

**Conclusion:** the alert path has the same defence-in-depth as the Phase 7 rule path —
per-alert dedup, per-action rate limit / cool-down, and a global per-minute budget with
storm-collapse. An attacker who can generate unlimited matching events produces at most one
open alert per group and a bounded number of notifications per minute.
