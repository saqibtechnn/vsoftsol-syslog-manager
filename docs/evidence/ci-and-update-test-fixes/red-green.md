# CI repair and update-checker test coverage — red/green evidence

Every change below was test-first. "Red" is the observed failing run before the fix.

## 1. CI failure: `NoBackdoorTests` — "No data found"

- **Cause.** The theory enumerates `publish/Web`, which exists only after an installer build; on a clean checkout
  (CI) it yielded no rows and xUnit fails an empty theory. It passed locally only because `publish/Web` was left over.
- **Red.** `publish/Web` renamed away, `dotnet test --filter NoBackdoorTests`:
  `Failed ... System.InvalidOperationException : No data found for ...PublishedAssembly_ContainsNoBannedBackdoorLiteral`
  (identical to the CI log of run 40582451277).
- **Fix.** With no publish folder, scan the product assemblies built next to the tests (test assemblies excluded,
  they contain the banned literals as data).
- **Green.** Without `publish/Web`: `Passed! 7/7`. With it: `Passed! 7/7`.

## 2. CI failure: SBOM job

- **Cause.** The job ran `dotnet CycloneDX ...`; the tool's command is `dotnet-CycloneDX` (log: `Run "dotnet tool
  restore" to make the "CycloneDX" command available.`) and the job never restored packages.
- **Fix.** `dotnet restore` + `dotnet-CycloneDX VSoftSol.Syslog.sln -o artifacts/sbom` in `.github/workflows/ci.yml`.
- **Verified locally** by installing the same tool version (6.3.1) to a scratch folder and running the command:
  `Found 184 packages ... Writing to ...bom.xml`. The workflow itself can only be proven by a CI run.

## 3. Intermittent CS2012 / branding file lock (parallel double build)

- **Cause.** Web referenced Service with `Properties=ExcludeAppSettingsFromPublish=true`. A per-reference property
  makes MSBuild build the referenced project as a second instance, concurrently with its normal build. A solution
  build therefore compiled Service twice at once (and Core too, running the branding generator twice).
- **Red (build).** `dotnet build src/VSoftSol.Syslog.Web --no-incremental`: `CSC : error CS2012: Cannot open
  '...BrandingGen.dll' for writing`. Also seen twice while running tests. After an interim fix limited to Service's
  own references, the solution build still failed: `error CS2012 ... VSoftSol.Syslog.Service.dll`.
- **Red (test).** `ProjectReferenceBuildTests.ProductProjectReferences_CarryNoPerReferenceBuildProperties...` failed:
  `Web.csproj -> ..\Service\...csproj sets Properties`.
- **Fix.** No reference carries build properties. Service's `appsettings.json` is never flowing content
  (`CopyToOutputDirectory`/`CopyToPublishDirectory` = Never) and Service copies its own file next to its own exe
  (`CopyOwnAppSettings` target).
- **Green.** `ProjectReferenceBuildTests` 2/2. Clean `dotnet build -c Release --no-incremental` of the solution:
  0 errors (the only warning is the existing, intended `VSOFTSOL-UPDATE-001` "no signing key yet").
  Output checks: Web bin `appsettings.json` == Web source; Service bin == Service source (the two sources differ);
  `dotnet publish` of Web: no NETSDK1152, published `appsettings.json` == Web source.
- **Note.** Service is the dev-only console collector and is never packaged or published on its own
  (`installer/Installer.wixproj` takes only the Web publish), so its publish output no longer carries appsettings.

## 4. `UpdateChecker` full verify-and-stage path is now tested

- **Gap.** The signing key is a compile-time constant, so `UpdateCheckerTests` could only cover "disabled" and "no
  key". Everything past the key gate was covered only by a manual rehearsal.
- **Seam.** An `internal` `UpdateChecker` constructor takes a public key; Service gets `InternalsVisibleTo` for the
  test assemblies. Production code never calls it (the DI factory uses the public constructor), so the shipped
  product still trusts only the baked-in vendor key. `UpdateSignatureVerifier` is unchanged.
- **Red.** `error CS1739: ... does not have a parameter named 'releasePublicKeyBase64'`.
- **Green.** 5 new tests with a throwaway keypair against a local fake release server: valid signed newer release is
  downloaded, hash-verified and staged (notification raised); tampered manifest rejected and the `.msi` is never
  requested; manifest signed by an untrusted key rejected; installer not matching the signed hash is not staged and
  leaves no file; a not-newer release reports up to date without downloading. `UpdateCheckerTests` 7/7.
- **Mutation check.** With the signature check disabled (`if (false && !signatureValid)`), the tamper and untrusted-key
  tests failed (2 of 7); reverted.
- **Side effect.** Service's `InternalsVisibleTo` makes its top-level-statements `Program` visible to the integration
  tests, ambiguous with Web's; `SyslogWebApplicationFactory` now uses `Components.App` as the Web-assembly marker.

## Full regression (final tree)

- Unit: `Passed! 1132/1132` (was 1130).
- Integration: `Passed! 895/895` (was 890; +5).
- `dotnet format --verify-no-changes`: clean. `dotnet build -c Release --no-incremental`: 0 errors.
- The GitHub workflow runs are not re-verified here; they need a push.
