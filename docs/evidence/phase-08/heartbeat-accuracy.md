# Phase 8 — heartbeat (device-silent) accuracy

PHASE_08 Validation & Evidence: "device stops at T; assert the alert fires within the
configured threshold ± one evaluation interval, and auto-resolves within one interval of
resumption."
PHASE_08 build item 2: DeviceSilent "is a headline differentiator … Honour each device's
own heartbeat threshold."

## Test

`AlertDeviceSilentTests.DeviceStopsSending_FiresAfterHeartbeat_ThenResumes_AutoResolves`
(virtual clock, `TickInterval = 5 min`, `IntervalSeconds = 300`):

| step | clock | state |
|---|---|---|
| device sends one heartbeat | T0 − 1 min | healthy |
| tick | T0 + 5 min | no open instance (`(now − lastSeen) = 6 min < 30 min heartbeat`) |
| 7 quiet ticks | T0 + 40 min | **instance opens** |
| assert | | `(now − T0)` ∈ **[30 min, 40 min]** — the 30-minute `heartbeat_minutes` threshold plus at most one 5-minute evaluation interval |
| device resumes (one event) | T0 + 40 min | |
| tick | T0 + 45 min | **instance auto-resolved** — resolution within one interval of resumption |

`AlertDeviceSilentTests.DeviceSilent_HonoursEachDevicesOwnHeartbeatThreshold`: two devices,
both last heard from a minute ago; 15 minutes later the device with `heartbeat_minutes = 10`
has an open instance and the device with `heartbeat_minutes = 240` does not — the threshold
is per-device, read straight from the `devices` registry by
`SqliteAlertWindowReader.DeviceLastSeenAsync` (falling back to the alert window only when a
device has no `heartbeat_minutes` set).

## How the accuracy bound arises

`AlertEvaluator.EvaluateDeviceSilent` fires when `(utcNow − lastSeenUtc).TotalMinutes >
thresholdMinutes` (or the device has never been heard from — `lastSeenUtc == null`). The
scheduler evaluates every `IntervalSeconds`, so the alert opens on the first tick after the
threshold elapses: firing time ∈ `[T + threshold, T + threshold + interval)`. Auto-resolve
runs in the same reconcile pass, so a resumed device clears on the next tick — within one
interval.

## One-click creation (UX gate)

`AlertWebTests.DeviceSilentOneClick_CreatesTheEstateWideAlert_ThenIsIdempotent`: the
"🔔 Alert me if this device goes silent" button on the device health card creates a single
estate-wide DeviceSilent alert (watches every monitored device, each honouring its own
heartbeat); pressing it again for another device is a no-op that tells the operator the
device is already covered. See `ux-gate.md`.
