# Phase 11 — verification

Exact commands and real, pasted output (TESTING_STANDARDS.md — "pasting output into chat
is not evidence; it must be committed").

## Build

```
$ dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Format

```
$ dotnet format --verify-no-changes
(exit 0 — after one fix pass; the same class of whitespace/brace-style findings every
prior phase has needed: IMPORTS ordering and IDE0011 "add braces to if")
```

## Dependency scan (SCA)

```
$ dotnet list package --vulnerable --include-transitive
The given project `VSoftSol.Syslog.Core` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Data` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Ingestion` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Rules` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Reporting` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Service` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Web` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.UnitTests` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.IntegrationTests` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.BrandingGen` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.Benchmarks` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.CrashProbe` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.IngestionProbe` has no vulnerable packages given the current sources.
The given project `VSoftSol.Syslog.ActionProbe` has no vulnerable packages given the current sources.
```

Zero new dependencies this phase — every new capability (TLS, SNMP BER decoding, TOTP,
ECDSA bundle signing, HTTP intake) uses only the in-box BCL.

## Full test suite

First full-solution run surfaced one flaky test-isolation bug (`ConfigBundleTests`, fixed —
see `known-issues.md`); the run below is the confirming clean re-run after the fix.

```
$ dotnet test -c Release
...
Passed!  - Failed:     0, Passed:  1041, Skipped:     0, Total:  1041, Duration: 1 m 16 s - VSoftSol.Syslog.UnitTests.dll (net8.0)
...
Passed!  - Failed:     0, Passed:   730, Skipped:     0, Total:   730, Duration: 6 m 25 s - VSoftSol.Syslog.IntegrationTests.dll (net8.0)
EXIT_CODE=0
```

**1041 unit / 730 integration = 1771/1771**, up from Phase 10's 900/667 (+141 unit, +63
integration; the config-bundle test-isolation fix removed the earlier run's 1 flake).

## Authorization matrix (the four new pages)

```
$ dotnet test -c Release --filter "FullyQualifiedName~AuthorizationMatrixTests"
Passed!  - Failed:     0, Passed:   134, Skipped:     0, Total:   134, Duration: 31 s
```

## Coverage (Ingestion / Rules / Reporting ≥ 80%)

Union line coverage across a clean coverage-instrumented unit run (1041/1041) and
integration run (730/730 — no discrepancy against the plain runs this phase):

```
VSoftSol.Syslog.Ingestion : 2231 / 2614 lines = 85.35%  PASS
VSoftSol.Syslog.Rules     : 1223 / 1450 lines = 84.34%  PASS (unchanged — no Rules changes)
VSoftSol.Syslog.Reporting : 288 / 336 lines   = 85.71%  PASS (unchanged — no Reporting changes)
```

Full cobertura XMLs: `coverage-unittests.cobertura.xml`, `coverage-integration.cobertura.xml`.

## Ingest throughput (re-run — MessageParser now branches on protocol, VendorExtractor's
pack list grows 8→17; TESTING_STANDARDS.md §5 re-run discipline applied out of caution
even though Phase 11 is not in the literal Phase 3/6/7/12 list), isolated (dev-vm-constraints)

```
$ dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --ingest-probe --frames 40000
[probe] start vendor=True seq=False streams=0 rules=0
warmup  vendor=True streams=0 : 40000 frames, 40000 rows, 6.19s => 6,465 msg/sec
MEASURE vendor=True streams=0 : 40000 frames, 40000 rows, 5.46s => 7,329 msg/sec

$ dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --ingest-probe --frames 40000 --novendor
[probe] start vendor=False seq=False streams=0 rules=0
warmup  vendor=False streams=0 : 40000 frames, 40000 rows, 2.12s => 18,910 msg/sec
MEASURE vendor=False streams=0 : 40000 frames, 40000 rows, 1.73s => 23,139 msg/sec
```

**RFC-only 23,139 msg/sec; vendor extraction against all 17 loaded packs 7,329 msg/sec** —
both comfortably clear the 5,000 msg/sec gate. The 17-pack vendor number is not a
regression from Phase 7's 8-pack ~5,300 msg/sec worst case (it is higher) — the probe's own
fixed message corpus resolves to an early-priority match for most of its sample messages,
so this is not a like-for-like worst-case comparison, but it does confirm the phase's two
ingest-path changes (a per-frame protocol branch check in `MessageParser.Parse`, and a
vendor-pack list more than double the previous size) do not push throughput anywhere near
the gate.

## New listener integration tests (isolated re-run for the record)

```
$ dotnet test -c Release --filter "FullyQualifiedName~TlsSyslogListenerTests|FullyQualifiedName~SnmpTrapListenerTests|FullyQualifiedName~WinEventLogListenerTests"
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11
```

## Config bundle tests (isolated re-run for the record, after the test-isolation fix)

```
$ dotnet test -c Release --filter "FullyQualifiedName~ConfigBundleTests"
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 1 s
```
