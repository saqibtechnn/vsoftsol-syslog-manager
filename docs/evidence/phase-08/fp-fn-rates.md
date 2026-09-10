# Phase 8 — false-positive / false-negative rate

> "An alerting system nobody has measured is an alerting system nobody will trust."
> — PHASE_08 Validation & Evidence

## Method

A version-controlled, hand-labelled dataset — `tests/fixtures/alerts/labelled-scenarios.json`
— pairs a synthetic event stream + an alert definition with a known-correct verdict
(`expectFire`). `AlertFalseRateTests.LabelledScenarios_MeasuredFalsePositiveAndFalseNegativeRates_AreZero`
replays each scenario against the real scheduler on a virtual clock (seed events → create
alert → advance the clock → one `TickAsync` → compare "open instances > 0" with the label),
then tallies:

| | predicted fire | predicted no fire |
|---|---|---|
| **should fire** | true positive | **false negative** |
| **should not fire** | **false positive** | true negative |

The fixture is not generated from the code under test (TESTING_STANDARDS §2.9) — it is a
literal JSON file authored by hand.

## Scenarios (12)

| # | Scenario | Type | Label |
|---|---|---|---|
| 1 | 6 matching in a 5-min window, threshold 5 | threshold | FIRE |
| 2 | exactly 5 matching, threshold 5 ("exceeds N") | threshold | no fire |
| 3 | 8 matching but 4 outside the window, threshold 4 | threshold | no fire |
| 4 | 5 matching in-window, threshold 4 | threshold | FIRE |
| 5 | grouped — group A breaches (4 ≥ 4), group B quiet | threshold | FIRE (A only) |
| 6 | no matching messages at all | threshold | no fire |
| 7 | 6 distinct source IPs, threshold 5 | distinct-count | FIRE |
| 8 | 5 distinct source IPs (one repeats), threshold 5 | distinct-count | no fire |
| 9 | expected "backup completed" log is missing | absence | FIRE |
| 10 | expected "backup completed" log is present | absence | no fire |
| 11 | 4 events, ungrouped, threshold 3 | threshold | FIRE |
| 12 | events present but none match the filter text | threshold | no fire |

## Result

```
scenarios=12  TP=6  TN=6  FP=0  FN=0
false-positive rate = 0.0%
false-negative rate = 0.0%
```

**Measured FP rate: 0.0 %. Measured FN rate: 0.0 %.**

The dataset is deliberately small and covers the decision boundaries (at/above/below
threshold, window edges, grouped vs quiet, distinct-with-repeat, absence both ways, filter
miss). It is not a statistical sample of production traffic — the pure `AlertEvaluator` +
the 5,000-case differential oracle (`oracle-divergence.md`) carry the "does the maths hold
for arbitrary inputs" load; this set proves the labelled cases an operator would reason
about are all correct end-to-end through the scheduler.
