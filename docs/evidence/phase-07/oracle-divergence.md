# Phase 7 — rules-matcher oracle divergence report

`RuleSetOracleTests.ProductionMatcher_AgreesWithTheNaiveMatcher_ForEveryGeneratedCase`
(seed `20260908`).

| | value |
|---|---|
| Generated rule sets | **200** (1–11 rules each: OR/AND filters over message / hostname / severity / facility / app / source_ip; ~⅓ with a time-of-day window; ~¼ device-group-restricted) |
| Generated events | **50 per set = 10,000** (varied message / host / app / severity / facility / device) |
| Reference matcher | an independent walk reusing the Phase 6 `NaiveConditionMatcher` for the filter; separate time-window + group logic |
| **Divergences** | **0** |

For every generated (rule set, event, clock) triple the production `RuleSet.Match` returned
exactly the same ordered list of rule ids (priority then id) as the naive reference. Zero
divergences. Compensating evidence for the blocked Stryker run (P3-3).
