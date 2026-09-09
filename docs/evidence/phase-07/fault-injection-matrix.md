# Phase 7 — fault-injection matrix (action × failure mode)

Every cell is an xUnit assertion (`ActionExecutorTests`, `ScriptSandboxTests`,
`OdbcActionTests`, `ActionDispatchServiceTests`). "Contained" = never throws past the
executor. "Classified" = the result is transient (dispatcher backs off + retries) or
permanent (dispatcher dead-letters). Every failure is written to the audit log and, per
`RuleIngestIsolationTests`, **none of them touch the ingest thread**.

| Action | Success | Refuse connection | Hang / timeout | 4xx / client error | 5xx / server error | Oversize | Non-zero exit | Notes |
|---|---|---|---|---|---|---|---|---|
| SendEmail   | ✓ delivered to an in-process SMTP sink | ✓ transient | — (20 s socket timeout) | ✓ 4xx → transient | ✓ 5xx → permanent | — | — | CR/LF stripped from subject + addresses |
| HttpWebhook | ✓ body posted to an `HttpListener` sink | ✓ transient | ✓ 1 s timeout → transient, caller not hung | ✓ 400 → permanent | ✓ 500 → transient (429/408 too) | ✓ response read capped at 64 KB | — | redirect (3xx) → permanent, not followed |
| RunScript   | ✓ exit 0, args echoed | ✓ can't-start → permanent | ✓ 1 s timeout → killed (tree), transient | — | — | ✓ `--spew` stdout capped | ✓ exit 7 → permanent, output captured | outside allow-list / `..` / symlink → refused |
| ForwardSyslog | ✓ UDP bytes received | ✓ transient | — (10 s connect timeout) | — | — | — | — | self-target → refused as a loop, nothing sent |
| WriteToFile | ✓ appended, rotates by size | — | — | — | — | — | ✓ IO error → transient; access denied → permanent | traversal / UNC / ADS / reserved → permanent |
| WriteToOdbc | (live round-trip → Phase 12, P7-3) | ✓ connect fail → transient (SQLSTATE 08/HYT) | ✓ 2 s timeout | — | ✓ bad statement → permanent | — | — | non-identifier table/column → permanent, no connection made |
| RaiseNotification | ✓ via the sink delegate | n/a | n/a | n/a | n/a | title capped at 300 / body 4000 | n/a | — |

Retry / dead-letter: `ActionDispatchServiceTests.Dispatcher_RetriesTransientThenDeadLetters`
— attempt 1 fails → `failed` with exponential back-off (virtual clock) → attempt 2 →
`MaxAttempts` reached → `dead` + an operator notification + an `action.deadlettered` audit
row.
