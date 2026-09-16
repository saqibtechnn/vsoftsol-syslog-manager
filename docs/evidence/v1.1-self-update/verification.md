# v1.1 — Self-update check and verified download (ADR 0021) — verification

Per the plan's own Verification section. All commands run from the repository root.

## Build

```
$ dotnet build -c Release
...
Build succeeded.

build\releasesigning.targets(42,5): warning VSOFTSOL-UPDATE-001: No release-signing public
key at ...\release-signing\public-key.txt — self-update signature verification is disabled
in this build (ReleaseSigningInfo.HasRealKey = false). See release-signing/README.md.
[src\VSoftSol.Syslog.Core\VSoftSol.Syslog.Core.csproj]

    2 Warning(s)
    0 Error(s)
```

The one warning is expected and by design: this repository has no real release-signing
keypair generated (`release-signing/public-key.txt` is absent — that is the vendor's own
credential-lifecycle decision, not something generated here), so the build correctly bakes
in a placeholder key and fails self-update verification closed. It appears twice because
both `dotnet build -c Release` project evaluations (the solution build and the branding
generator's own upstream Core reference) surface the same MSBuild warning; there is nothing
to deduplicate — it is one warning from one target, reported once per project that pulls in
`Core`.

## Unit tests

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --no-build
Passed!  - Failed:     0, Passed:  1113, Skipped:     0, Total:  1113, Duration: 1 m 16 s
```

Includes `LayeringTests` (confirms the `Data`/`Rules` layering fix — see `red-green.md`),
`CoreArchitectureTests` (confirms `Core`'s `System.Net`/`System.Data` isolation was never
violated), and the 41 self-update unit tests (`ProductVersionTests`,
`UpdateManifestValidatorTests`, `UpdateSignatureVerifierTests`).

## Integration tests — self-update subset

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Updates" --no-build
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 10 s
```

`Migration011Tests`, `SqliteUpdateSettingsStoreTests`, `GitHubUpdateClientTests`,
`UpdateCheckerTests`, `UpdateWebTests`.

## Integration tests — full suite (regression check)

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --no-build
Passed!  - Failed:     0, Passed:   848, Skipped:     0, Total:   848, Duration: 8 m 26 s
```

Zero failures across the entire suite — no regression in any prior phase or v1.1 item from
this feature's changes (the `Data`/`Service` layer moves, the two new project references
removed/added, or the `CurrentUserAccessor` fix).

## Style gate

```
$ dotnet format --verify-no-changes
(exit 0 — no diffs)
```

## Installer packaging — confirmed untouched

```
$ dotnet build installer/Installer.wixproj -c Release
Installer -> ...\installer\bin\x64\Release\en-us\VSoftSolSyslogManagerSetup.msi
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

This feature adds no installer-relevant files (no new bootstrap config, no new registry
values, no new Windows Service) — the MSI still builds clean, confirming packaging is
unaffected.

## Release-signing tool — smoke-tested end to end

`tools/VSoftSol.Syslog.ReleaseSigning` was exercised manually against a scratch directory
(outside this repository) before being wired into the solution:

- `generate-keys <scratch-repo-root>` — wrote a valid base64 `SubjectPublicKeyInfo` to
  `release-signing/public-key.txt` under the scratch root and printed a matching private key
  to the console only (confirmed nothing was written to disk for the private half).
- `sign-release <fake.msi> 1.1.0 <msi-url> <notes-url> <private-key>` — computed the correct
  SHA-256 of the fake MSI, built and signed an `UpdateManifest`, and wrote a well-formed
  `update-manifest.json` (`documentJson` + `signatureBase64`) next to it.

Both subcommands exit 0 on success and produce output matching `UpdateManifestValidator`'s
schema. No real production keypair was generated in this repository — see
`release-signing/README.md` and this ADR's Decision 2 for why that is a deliberate,
out-of-scope-for-this-session choice, not an oversight.

## Manual end-to-end check with a real signed release

**Not performed in this session** — per the plan's own "Explicitly accepted / deferred"
section, this requires the vendor to generate a real production keypair (a credential
custody decision this session correctly declined to make on the vendor's behalf) and
publish an actual signed GitHub release. Recorded here as the one item from the plan's
Verification section still outstanding, exactly as the plan itself anticipated — not a
gap discovered after the fact.

## Gates checklist (per the plan's Verification section)

- [x] `dotnet build -c Release` warning-clean (2 expected `VSOFTSOL-UPDATE-001` warnings,
      by design — no real key configured)
- [x] Every new test observed RED then GREEN (`red-green.md`)
- [x] Full suite green — unit 1113/1113, integration 848/848
- [x] `CoreArchitectureTests` still green — Core boundary not violated
- [x] `LayeringTests` still green — the one violation this feature caused was caught and
      fixed, not silenced (`red-green.md`)
- [x] `dotnet format --verify-no-changes` clean
- [x] New ADR (0021) + `THREAT_MODEL.md` (B6) + `ASVS-checklist.md` + `SECURITY_REVIEW.md`
      sections committed
- [ ] Manual end-to-end check with a real signed test release — deferred to the vendor, see
      above
