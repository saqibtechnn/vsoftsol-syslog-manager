# Phase 3 — oracle / differential comparison

PHASE_03 Validation: "run the full fixture corpus through `rsyslog` or `syslog-ng` (in a
container or WSL) and compare extracted fields against ours."

## What blocked the live oracle

This build host has **no Docker, no WSL, and no `rsyslogd`** (`wsl --status` → "not
installed"; `which docker` → not found). A live rsyslog/syslog-ng comparison was not
possible here. This is recorded as an accepted deviation with a substitute, per CLAUDE.md.

## The substitute — an independent implementation

`OracleDifferentialTests.RegexReferenceParser` is a **second, independent RFC 3164 / 5424
parser** written to a different strategy than the production parser:

| | production | reference oracle |
|---|---|---|
| approach | hand-written `ReadOnlySpan<char>` state machine, zero regex on the header path | pure regex (`^<pri>1 ts host app procid msgid rest$` etc.) |
| author intent | speed, allocation-light | simplicity, obviously-correct |

The test runs **all 150 fixtures the reference can parse** through both and compares
`facility`, `severity`, `hostname`, `app_name`, `proc_id`, `msg_id`, and `message`.

## Result

```
compared 150 fixtures against the independent reference
0 divergences
```

Every divergence found during development was a bug in one implementation or the other
(D3-3 in production; two in the reference regex), fixed, and re-run to zero. This satisfies
the phase's rule — "any divergence is either a bug or a documented deliberate difference;
there is no third option" — with an independent implementation standing in for the
container.

## Follow-up

A live rsyslog/syslog-ng comparison is added to the Phase 12 pre-release checklist, to run
on a host that has a container runtime. Carried as `known-issues` P3-1.
