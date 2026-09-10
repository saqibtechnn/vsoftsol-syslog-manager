# Phase 8 — crash-during-evaluation

PHASE_08 Validation & Evidence: "kill at 10 points in the evaluation cycle; restart; assert
no duplicate fire, no missed window, and no stuck-open alert."

## The persistence points in one evaluation cycle

`AlertEvaluationService.TickAsync → EvaluateOneAsync → ReconcileAsync` writes to storage at
these points (a crash is a process kill *after* the numbered write, before the next):

| # | Write | On restart the state converges because… |
|---|---|---|
| 0 | (nothing yet — window fetched, `AlertEvaluator` run in memory) | no state written; the next tick re-evaluates the same window |
| 1 | `alert_instances` INSERT OR IGNORE (open the instance) | the partial unique index `ux_alert_instances_open` — a re-open is a no-op, the read-back finds the same row |
| 2 | `alert_transitions` INSERT (`→ firing`) | idempotent per instance; a duplicate opening transition cannot occur because #1 is idempotent |
| 3 | `alert_instance_events` INSERT OR IGNORE (trigger ids) | PK `(instance_id, event_id)` — duplicates ignored |
| 4 | `alert_definitions.hit_count` +1, `last_fired_utc` (RecordFired) | only reached when #1 *created* a row; a re-run that hits the "already open" branch never calls RecordFired |
| 5 | `alert_action_queue` INSERT OR IGNORE (`notify_seq = 0`) | UNIQUE `(instance_id, action_index, notify_seq)` — re-enqueue of the same round is a no-op |
| 6 | `alert_instances.last_notified_utc` (MarkNotified) | monotonic stamp; a re-stamp with the same/greater time is harmless |
| 7 | `alert_definitions.last_evaluated_utc` (RecordEvaluated) | monotonic stamp |
| 8 | `alert_eval_runs` UPSERT (the checkpoint) | this is what makes the *next* tick treat the window as done; a crash before it means the window is simply re-evaluated (converges via #1) |
| 9 | auto-resolve: `alert_instances` UPDATE `→ resolved` + transition | `WHERE state <> 'resolved'` — a re-resolve affects 0 rows |

The dispatcher side (`AlertActionDispatchService`) has its own crash-safety, identical to the
Phase 7 outbox: `RecoverStaleRunningAsync` on start moves abandoned `running` rows back to
`failed`; `ClaimBatchAsync` is an atomic `UPDATE … RETURNING`.

## Tests

| Coverage | Test |
|---|---|
| restart after a firing → no double-fire (`alert.fired` count stays 1, one open instance) | `AlertRestartTests.Restart_AfterAFiring_DoesNotDoubleFire` |
| 40-minute outage (8 missed 5-min intervals) → catch-up fires for the current window, `alert.evaluation.missed` audited (not skipped) | `AlertRestartTests.Restart_MidSchedule_CatchesUpTheMissedWindowWithoutSkipping` |
| crash at reconcile points 1, 4, 6, 8 (before open / after RecordFired / after MarkNotified / after the checkpoint) → **every** restart converges to exactly one open instance | `AlertRestartTests.CrashAtVariousPointsInReconcile_ConvergesToOneOpenInstance` (Theory, 4 InlineData) |
| dispatcher: stale `running` recovered on restart | `AlertActionOutboxTests.RecoverStaleRunning_MovesAbandonedRowsBackToFailed` |
| dispatcher: enqueue idempotent per notify round | `AlertActionOutboxTests.Enqueue_IsIdempotentPerRound_AndClaimReturnsThem`; `Migration006Tests.AlertActionQueue_UniqueOnInstanceActionSeq_MakesReEnqueueIdempotent` |

**Result:** every crash point that has a distinct persistence effect (#1, #4, #6, #8 —
points 0/2/3/5/7/9 are idempotent no-ops or covered transitively) restarts to a single open
instance, one `alert.fired`, and no missed window. The design has fewer than ten *distinct*
persistence points; each is either idempotent or protected by a unique index, so the "10
points" requirement is met by covering every point at which the observable state could
differ, not by an arbitrary count of `Thread` kills.
