# Phase 7 — rate-limit accuracy

`RuleRuntimeTests.Apply_RateLimit_ProducesExactlyTheConfiguredNumberOfDispatches`

| Setting | Input | Dispatched | Rate-limited |
|---|---|---|---|
| `MaxPerWindow = 5`, `WindowSeconds = 60`, no cool-down | 1,000 matching messages (virtual clock frozen) | **5** | **995** |

`Apply_RateLimit_WindowSlides_AllowsMoreAfterTheWindow` — 2 per 10 s: 2 dispatched, the 3rd
rate-limited; after `clock.Advance(11s)` a 4th is allowed. `Apply_Cooldown_BlocksUntilTheGapElapses`
— `CooldownSeconds = 30`: first dispatched, next blocked, still blocked at +15 s, allowed at
+31 s. `Apply_GlobalBudget_CollapsesExcessDispatchesIntoASingleSummary` —
`GlobalActionsPerMinute = 10`, 100 messages: **10 dispatched, exactly 1 storm summary**.

The counts are asserted against the runtime's own decision (`RuleOutcome.Dispatches.Count` /
`RateLimitedCount`); the fault-injection tests additionally verify the sink's own record for
the delivered ones (an `HttpListener.RequestCount`, an `SmtpSink.ReceivedData` list).
