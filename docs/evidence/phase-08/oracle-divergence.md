# Phase 8 — differential oracle

Stryker mutation testing does not run on this SDK-only host (P0-2 / P3-3). The compensating
evidence for the alert engine, following the Phase 6 routing-oracle and Phase 7
rules-oracle precedent, is two differentials.

## 1. Firing decision — `AlertEvaluatorOracleTests`

An independent naive re-implementation of the firing decision (inline in the test, no shared
code with `AlertEvaluator`) must agree with the production evaluator for **5,000** generated
`(CompiledAlert, AlertWindowData)` pairs across all four evaluation types (threshold grouped
& ungrouped, distinct-count, absence, device-silent), seed `20260909`.

```
5000 comparisons, 0 divergences
```

## 2. Fetch path — `AlertWindowReaderTests.CountByGroup_AgreesWithInMemoryGrouping`

The hybrid model (ADR 0016) has two ways to count a window: a SQL `GROUP BY` aggregate (the
no-filter fast path) and an in-memory grouping of streamed rows (the filtered path). They
must never disagree. The test seeds **400** events with random hostnames over a 10-minute
window and asserts:

```
SQL GROUP BY hostname  ==  events.GroupBy(e => e.Hostname).ToDictionary(g => g.Key, g => g.Count())
```

byte-for-byte on the per-group counts, with the same `COALESCE(col, '(none)')` null handling
on both sides (`AlertGrouping.KeyFor` mirrors the SQL expression). 0 divergences.

## Coverage of the pure engine

Beyond the oracles: `AlertCompilerTests` (16 cases — every validation rule, per-type
constraints, action delegation), `AlertEvaluatorTests` (13 — the boundary matrix),
`AlertRuntimeTests` (6 — the budget), `SearchToConditionTranslatorTests` (11 — the
promote-to-alert translation). `AlertJson` round-trip is covered by
`AlertPersistenceTests.Alert_RoundTripsThroughTheStore` (filter, actions, id lists, severity,
type all survive a create → read). The `AlertEvaluator` itself is
~90 lines of pure branching with no hidden state; the 10,000-case combined oracle surface
plus the labelled FP/FN set (`fp-fn-rates.md`) is the mutation-score substitute, carried to
a CI host with the VsTest adapter (P3-3).
