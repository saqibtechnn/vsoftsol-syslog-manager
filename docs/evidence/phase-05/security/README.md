# Phase 5 — security evidence

Per `SECURITY_STANDARDS.md` and the PHASE_05 "Security Validation" section.

## Gates

| Gate | Result | Evidence |
|---|---|---|
| SAST (analyzers as errors; CodeQL in CI) | PASS | `dotnet build -c Release` warning-clean, 13 projects |
| SCA (`dotnet list package --vulnerable --include-transitive`) | PASS | `sca-vulnerable.txt` — no vulnerable packages, no new dependency (query language hand-written; export uses BCL `System.Text.Json`) |
| Secrets scan | PASS | no secrets in source; export/audit never carry secret material |
| Branding literal guard | PASS | grep for brand values under `src/` returns only `BrandingInfo.g.cs` |

## PHASE_05 security validation

| Item | How verified | Result |
|---|---|---|
| **Stored XSS through the full path** — OWASP filter-evasion corpus | `StoredXssMatrixTests` — 10 payloads ingested verbatim (raw bytes + `message` round-trip byte-identical), rendered HTML-encoded on the pre-rendered results grid, unicode-escaped in the JSON export with the decoded value still exact | PASS |
| Expanded row / context view / live tail encoding | Share the identical Razor auto-encoding path as the grid (`@Event.Message`, `@field.Value`, `<pre>@RawText</pre>`) — asserted structurally (bunit unavailable, P4-3) | PASS (path-shared) |
| **CSV formula injection** — `= + - @ TAB CR` | `SearchExportWriterTests` + `SearchExportSecurityTests` — cell prefixed with `'` in the CSV; the stored `message` and `raw_message` are byte-identical | PASS (both halves) |
| **Query injection** — SQL fragments, FTS5 operator abuse, nested quotes, unicode normalization, 10 KB, stacked statements | `SearchCompilerTests` (no user bytes in SQL text) + `SearchInjectionTests` (no data mutated, no exception, malformed → user error, no full scan) | PASS |
| **Golden-oracle differential** — optimised SQL vs brute-force matcher | `SearchOracleTests` — 500 generated queries over a 700-event corpus, **0 divergences** (`../oracle-divergence.md`) | PASS |
| **IDOR** — saved searches, column layouts by another user's id | `SavedSearchStoreTests` — non-owner cannot read a private search, cannot edit/delete a shared one, cannot touch another user's layouts | PASS |
| **Scope bypass via query** — sort fields, wildcards, negation, export params | `SearchScopeTests` (7 query shapes incl. wildcard, `NOT`, naming the out-of-scope stream, match-all) + `SearchExportSecurityTests` (export stream is scoped) | PASS |
| **Export DoS** — request 50M rows | `SearchExportSecurityTests` — stays streamed (two connections, page-hydrated), capped at `ExportMaxRows` | PASS |
| Query plan assertions — 10 common shapes | `SearchQueryPlanTests` — `EXPLAIN QUERY PLAN` uses an index for every shape; never `SCAN events`; FTS shapes hit `events_fts` | PASS |
| Export written to the audit log | `SearchWebTests.Export_Csv_StreamsRowsAndWritesTheAuditLog` — `AuditActions.Export` row with actor, format, row count, query | PASS |
| DAST (OWASP ZAP) | NOT RUN — no browser/Docker (P4-1). Compensating pipeline assertions (`SearchWebTests`) run auth / redirect / content-type / 400-on-bad-query against real Kestrel over HTTPS. Carried to Phase 12 / CI. | carried |

No Critical, High, or Medium findings. No `TODO(phase-N)` markers in shipping code.
