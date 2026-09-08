# Phase 6 — stream routing oracle divergence report

`StreamRoutingOracleTests.ProductionRouter_AgreesWithTheNaiveMatcher_ForEveryGeneratedMessage`
(seed `20260908`).

| | value |
|---|---|
| Generated stream definitions | **50** (1 catch-all + 49 rule streams: equals / contains / starts-with / ends-with / regex / numeric range / in-list, in AND & OR groups) |
| Generated messages | **10,000** (varied severity, facility, hostname, app, message body, extracted fields) |
| Reference matcher | `NaiveConditionMatcher` — an independent tree walk, no shared code with `ConditionCompiler` / `ConditionEvaluator` |
| **Divergences** | **0** |

For every generated message the production `StreamRouter.Route` returned exactly the set
`{catch-all} ∪ {streams whose rule the naive matcher accepts}`. `router.CompileErrors` was
empty (the generator only emits valid rules). Zero divergences.

Companion assertions in the same suite: a message matching no rule routes to the catch-all
only; a message crafted to match three specific streams is in exactly those three plus the
catch-all; a disabled stream never routes.
