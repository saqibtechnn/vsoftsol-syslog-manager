# Phase 0 — Known issues

| # | Issue | Impact | Target |
|---|---|---|---|
| P0-1 | **Coverage threshold not yet enforced.** `Ingestion`, `Rules`, `Reporting` are Phase-0 shells (a marker type each), so an 80% line-coverage gate is vacuous. Overall line coverage is ~57% (`BrandingGen` 87%, `Core` schema partially covered). | None — no logic to leave untested. | Enforced from Phase 1 (Data) and per phase thereafter; CI step is present as a placeholder. |
| P0-2 | **Stryker.NET does not complete a mutation run on this host.** Tool is pinned (`.config/dotnet-tools.json` → `dotnet-stryker 4.16.0`) and configured (`stryker-config.json`: target `Events/SyslogPriority.cs`, test project `UnitTests`, break threshold 70). Its bundled VsTest runner reports "did not report any test" for the xUnit projects on this SDK-only environment, though `dotnet test` runs all 32 fine. See `stryker-output.txt`. | None for Phase 0 — `SyslogPriority` is ~10 lines and there is no parser/rules/retention logic to score yet. FsCheck property tests **do** run in the suite. | Resolve the runner environment in Phase 3, where Stryker gets its first real target (the parser) and the ≥70% mutation gate is enforced (TESTING_STANDARDS.md §3). |
| P0-3 | **CSP still allows `'unsafe-inline'` on `style-src`.** Needed for the Blazor error-UI inline style block in Phase 0. | Low — localhost/LAN admin tool, no untrusted script path. Documented in ASVS-checklist.md (V14.4) and THREAT_MODEL.md (B2). | Phase 4: per-response nonces, remove `unsafe-inline`, assert the full header set. |
| P0-4 | **`SerilogBootstrap` falls back to `%TEMP%\syslog-manager\logs`** if the configured data directory cannot be created. | Intentional resilience so logging never blocks startup; a misconfigured data dir is surfaced on stderr. | Phase 4 surfaces data-directory health in the UI; Phase 12 installer provisions and ACLs the real directory. |
| P0-5 | **`ILogRepository` / `IAuthenticationProvider` are specified but unimplemented.** | Expected for Phase 0 (`// services.Add…` lines are commented in the composition root, not `TODO(phase-N)` stubs in shipping paths). | `SqliteLogRepository` — Phase 1; `LocalAuthenticationProvider` — Phase 4. |

No `TODO(phase-N):` markers exist in shipping code paths. The one forward-looking comment
(`SecurityHeadersMiddleware` CSP → Phase 4) is tracked as P0-3 above.
