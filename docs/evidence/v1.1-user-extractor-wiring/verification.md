# v1.1 — user-authored extractors wired into ingest (P5-3 closed) — verification

Real, observed output from this environment, per TESTING_STANDARDS.md / this project's
Definition of Done. Not a numbered phase (`START_HERE.md` has no Phase 13), held to the
same evidence bar.

## Build — warning-clean

```
$ dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Style gate

```
$ dotnet format --verify-no-changes
(no output — exit 0, clean)
```

## New tests — targeted runs

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~VendorExtractorTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 36 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~UserExtractorLoaderHostedServiceTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 383 ms

$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~MessageParserTests"
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 243 ms
```

11 genuinely new tests (6 `VendorExtractorTests` + 4 `UserExtractorLoaderHostedServiceTests`
+ 1 new `MessageParserTests` case), all green. See `red-green.md` for the observed-RED
detail on each.

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1067, Skipped: 0, Total: 1067, Duration: 1 m
```

1060 (pre-existing) + 7 new (6 `VendorExtractorTests` + 1 `MessageParserTests` case).

## Integration tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 799, Skipped: 0, Total: 799, Duration: 6 m 51 s
```

795 (pre-existing) + 4 new (`UserExtractorLoaderHostedServiceTests`). Fully clean this run —
neither of the two previously-observed load-sensitive dev-VM flakes
(`ConcurrencyTests`/`WalCrashConsistencyTests`, both already documented as environmental in
`docs/evidence/v1.1-listener-identity-linkage/verification.md` and
`docs/evidence/phase-12/known-issues.md`) reproduced on this run.

## Security

Genuinely new ingest-path attack-surface consideration, addressed rather than dismissed:
an Operator-saved pattern (Settings → Pattern tester, `AuthPolicies.Operate`) now runs
against every ingested message — live, unauthenticated, attacker-controllable network input
— not just a sample the operator pasted into the tester. Mitigation is not new invention:
`UserExtractorLoaderHostedService` compiles every saved pattern through the exact same
`GrokLibrary`/mandatory-match-timeout ReDoS guard every vendor `.pack` file already relies on
(PHASE_03 Security Validation) — same regex engine, same timeout policy, same
`RegexOptions.Compiled` hot-path treatment. A malformed saved pattern (the store itself never
validated regex syntax; only the tester's live preview does) is logged and skipped at
startup — it cannot take the collector down, mirroring `PatternPackLoader`'s identical
contract for a malformed `.pack` file. No privilege escalation: authoring an extractor still
requires the same `AuthPolicies.Operate` gate Phase 5 already had, and an Operator already
authors streams (Phase 6) and rules (Phase 7) that run on this exact same ingest path today
— this closes a "built but inert" gap in an existing, already-reviewed trust boundary, it
does not open a new one. Full detail in `docs/security/ASVS-checklist.md` and
`docs/security/SECURITY_REVIEW.md`'s new v1.1 sections.

## UI verification

`PatternTester.razor`'s stale copy ("These are applied by the collector's extraction
pipeline once stream routing is configured (Phase 6).") — inaccurate since Phase 6 shipped
long ago and this was never actually wired in — is corrected to describe the real, current
behavior (runs on every message; takes effect on the next collector restart). This is a
copy-only change to an already-shipped, already-verified page (no new control, no new route,
no new interaction) — re-verifying the whole page through this sandbox's scripted-curl
first-run-wizard workaround (used for the two prior v1.1 UI items, `docs/evidence/v1.1-live-
listener-ports/verification.md` and `docs/evidence/v1.1-listener-identity-linkage/
verification.md`) would exercise nothing this change touches. The corrected string was
proofread in the diff instead.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (v1.0.0's own text left untouched; P5-3
  was an internal `PROGRESS.md`/`known-issues.md` tracking item, never a user-documented
  v1.0.0 limitation, so there is no historical bullet to preserve here).
- `PROGRESS.md` — new v1.1 log entry; "Deferred items across all phases" table's P5-3 row
  struck through with **DONE (v1.1)**, mirroring the table's own established convention;
  "Current state" bullet updated to list all five closed v1.1 items.
- `docs/security/ASVS-checklist.md` / `docs/security/SECURITY_REVIEW.md` — new v1.1 sections
  documenting the ingest-path trust-boundary analysis above (the historical Phase 5 rows in
  both documents are left untouched, per this project's "never rewrite history" convention).
- `src/VSoftSol.Syslog.Web/Components/Pages/Search/PatternTester.razor` — stale copy fixed.

## Conclusion

P5-3 (`docs/evidence/phase-05/known-issues.md`) is closed: every enabled extractor saved
from the Settings → Pattern tester now actually runs against every ingested message —
including, and especially, messages from a device with no built-in vendor pack at all, which
is exactly the gap this feature exists to close (CLAUDE.md constraint 4). Zero regressions:
unit 1067/1067, integration 799/799, both fully clean on this run.
