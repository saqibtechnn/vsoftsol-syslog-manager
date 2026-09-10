# Phase 8 — boundary matrix

PHASE_08 Validation & Evidence: "exactly at threshold, one below, one above, at window
edges, across DST transitions, across midnight UTC, and with events arriving out of order."

Windows are expressed in **UTC seconds** (`now` and `now − windowSeconds`), never in a
wall-clock or local-time expression — so a DST transition is a non-event by construction.
The matrix proves it.

| Case | Test | Result |
|---|---|---|
| one below threshold (4 events, threshold 5) | `AlertBoundaryMatrixTests.ThresholdBoundary(4,5,false)` | no fire ✓ |
| exactly at threshold (5 events, threshold 5) | `AlertBoundaryMatrixTests.ThresholdBoundary(5,5,false)` | no fire ✓ ("exceeds N") |
| one above threshold (6 events, threshold 5) | `AlertBoundaryMatrixTests.ThresholdBoundary(6,5,true)` | fire ✓ |
| window edges — event at the inclusive start counts; at the exclusive end does not | `AlertBoundaryMatrixTests.WindowEdges_EventAtTheInclusiveStartCounts_AtTheExclusiveEndDoesNot` | observed = 2 (the two in `[start, end)`), the `< start` and `== end` events excluded ✓ |
| across midnight UTC — a 5-minute window spanning `23:59 → 00:02` | `AlertBoundaryMatrixTests.AcrossMidnightUtc_TheWindowStillSpansCorrectly` | all three events counted ✓ |
| across a DST transition — US spring-forward 2026-03-08 07:00 UTC | `AlertBoundaryMatrixTests.AcrossADstTransition_IsANonEvent_BecauseWindowsAreUtc` | fires normally; the wall-clock jump does not shift the UTC window ✓ |
| out-of-order arrival — events inserted newest-first, one an hour old | `AlertBoundaryMatrixTests.OutOfOrderArrival_CountsByReceivedTime_NotInsertionOrder` | observed = 2 (the hour-old event is outside the window regardless of insertion order) ✓ |
| device-silent, exactly at the heartbeat threshold | `AlertEvaluatorTests.DeviceSilent_ExactlyAtThreshold_DoesNotFire` | no fire ✓ (fires strictly *after* N) |
| truncated window scan that already breaches | `AlertEvaluatorTests.Threshold_TruncatedWindow_ThatAlreadyBreaches_StillFires` | fires ✓ (a lower-bound count that already exceeds the threshold is still a breach) |

All boundary cases pass.
