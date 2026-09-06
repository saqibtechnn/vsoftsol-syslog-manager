# Phase 2 — red-before-green evidence

TESTING_STANDARDS.md §2.1: every test was observed failing for the correct reason before
the implementation existed. The ingest path is one interlocking mechanism, so red was
captured by reverting each component to a no-op and running the tests that depend on it,
rather than one commit per test.

## Component knock-outs and the failures observed

| Component reverted to a no-op | Tests that went red | Representative failure message |
|---|---|---|
| `SyslogStreamFramer.Append` emits nothing | `SyslogStreamFramerTests` (11) | `Expected collection to be equal to {"<13>one", "<13>two", "<13>three"}, but found empty collection.` |
| `SyslogStreamFramer` octet path (no length validation) | `SyslogStreamFramerTests.OctetCountedFraming_NonNumericLength_ThrowsFramingException` | `Expected a <FramingException> to be thrown, but no exception was thrown.` |
| `PerSourceRateLimiter.Check` always returns `Allow` | `PerSourceRateLimiterTests` (5), `RateLimitTests` (5), `IngestionSecurityTests.Flood_*` | `Expected the enum to be Decision.Drop {value: 2}, but found Decision.Allow {value: 0}.` |
| `UdpSyslogListener` drops the frame instead of calling `FrameIntake` | `UdpIngestionTests` (4), `WireFuzzTests` | `Expected value to be 100000L … but found 0L (difference of -100000).` / `Expected value to be 3L, but found 0L.` |
| `DiskSpillQueue.LeaseAsync` always returns `null` | `DiskSpillQueueTests` (drain cases), `BackpressureSpillTests`, `ChaosTests`, `KillRecoveryTests` | `System.TimeoutException : The operation has timed out.` (spill never drains → `DrainAsync` never completes) |
| `FrameCodec.TryDecode` always returns `false` | `FrameCodecTests` (5), `DiskSpillQueueTests` | `Expected ok to be true, but found False.` |
| `IngestionPipeline` never commits | every ingestion integration test | `Expected value to be 5000L, but found 0L.` |

All knock-outs were reverted and the full suite is green (`test-output.txt`).

## Defects found and fixed during the phase (defect protocol, TESTING_STANDARDS.md §6)

| # | Defect | Found by | Root cause | Fix |
|---|---|---|---|---|
| D2-1 | An oversized UDP datagram was **lost**, not stored truncated | `UdpIngestionTests.Udp_OversizedDatagram_IsStoredTruncated_NeverDropped` (red) | The receive buffer was sized to `MaxMessageBytes + 1`; a larger datagram made `ReceiveFromAsync` raise `SocketError.MessageSize` and the datagram was discarded | Always rent ≥ 65 536 bytes (the max UDP payload), receive the whole datagram, then trim to `MaxMessageBytes` with the `truncated` flag |
| D2-2 | Graceful shutdown could **hang forever** if the writer was wedged | `IngestionLifecycleTests.StopAsync_WhenDrainCannotFinishInTime_*` (hung) | `IngestionPipeline` committed with `CancellationToken.None` and, after the drain timeout, `IngestionHostedService` still `await`ed the pipeline task to completion | Pass the stopping token to the commit; on cancellation, flush the remaining in-memory frames to the (durable) spill queue and exit; the timeout now truly bounds shutdown |

Both defects have a permanent regression test in the suite.
