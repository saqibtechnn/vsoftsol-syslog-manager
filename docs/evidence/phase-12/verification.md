# Phase 12 — verification

Real, pasted output from this session. Commands run from the repository root unless noted.

## Build

```
$ dotnet build -c Release
...
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Format

```
$ dotnet format --verify-no-changes
(exit code 0 — no output means no violations)
```
(One violation was found and fixed mid-phase — an import-ordering issue in the rewritten
`SyslogWebApplicationFactory.cs` — via `dotnet format`, then reverified clean.)

## Full test suite

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed! - Failed: 0, Passed: 1060, Skipped: 0, Total: 1060, Duration: 1 m 47 s
```
(Up from 1041 at the end of Phase 11 — 19 new pure-logic tests this phase: 6
`RetentionPresetsTests`, 5 `VendorSupportDocumentParserTests` + 8
`VendorSupportDocumentParserRealDocumentTests`.)

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Failed! - Failed: 1, Passed: 755, Skipped: 0, Total: 756, Duration: 7 m 59 s
```
The one failure is the pre-existing, already-documented `P2-5` load-dependent flake
(`WalCrashConsistencyTests`'s hard-kill soak test) — its own `iteration N: X committed rows
survived, integrity ok` log lines matched the run's output exactly, and this flake's
disposition ("tolerated, monitor") has been unchanged and explicitly accepted since Phase 2.
Not a regression introduced by this phase; every one of this phase's own new tests, and
every previously-green test, passed.

A **later** full re-run (after the About.razor fix below) additionally reproduced
`P7-5` (`LocalAuthenticationProviderTests.AuthenticateAsync_UnknownUserVsWrongPassword_
TakeComparableTime`, the Argon2 decoy-timing ratio check) — investigated fresh rather than
dismissed by old precedent, and confirmed a pre-existing, load-sensitive test unrelated to
any Phase 12 code (`LocalAuthenticationProvider` is untouched this phase); the property it
protects was independently verified correct by reading the source, not just trusting the
one flaky timing assertion. Full detail in `known-issues.md`.

Targeted regression pass immediately after the single-service architecture correction
(before the full run above), confirming every directly-affected area:
```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~FirstRun|FullyQualifiedName~WebServiceIdentity|FullyQualifiedName~ScopeChokepoint|FullyQualifiedName~AuthorizationMatrix|FullyQualifiedName~WebCertificateProvisioning|FullyQualifiedName~BootstrapConfigOverrides"
Passed! - Failed: 0, Passed: 158, Skipped: 0, Total: 158, Duration: 36 s
```

## Coverage gate (Ingestion / Rules / Reporting ≥ 80% line)

CLAUDE.md's documented coverage command (`/p:CollectCoverage=true`) targets
`coverlet.msbuild`; this repo's test projects reference `coverlet.collector` instead (a
pre-existing setup, not changed this phase), so that invocation silently collects nothing.
The actual working invocation for this repo:
```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --collect:"XPlat Code Coverage"
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --collect:"XPlat Code Coverage"
```
See `coverage-summary.txt` in this evidence folder for the full per-assembly union result
(identical to Phase 11's baseline — Phase 12 adds no code to any of these three
assemblies; the gate was re-run to confirm no regression, not because this phase's own
changes touch them).

## SCA (dependency vulnerability scan)

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
Zero findings across every project.

## The installer

```
$ dotnet publish src/VSoftSol.Syslog.Web -c Release -r win-x64 --self-contained false -o publish/Web
...
VSoftSol.Syslog.Web -> ...\publish\Web\

$ dotnet build installer -c Release
...
Installer -> ...\installer\bin\x64\Release\en-us\VSoftSolSyslogManagerSetup.msi
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

MSI validated:
```
$ wix msi validate bin/x64/Release/en-us/VSoftSolSyslogManagerSetup.msi
(only the deliberately-suppressed ICE60 — unversioned resource files — reported; every
other standard ICE clean)
```

MSI decompiled and its structure confirmed, most load-bearingly:
```
<ServiceInstall Id="ServiceInstall" Name="VSoftSol Syslog Manager"
                DisplayName="VSoftSol Syslog Manager"
                Account="NT SERVICE\VSoftSol Syslog Manager" ... />
<File Id="ServiceExe" Name="VSoftSol.Syslog.Web.exe" KeyPath="yes" ... />
```
— exactly one service, registered under the plain product name, running
`VSoftSol.Syslog.Web.exe`. Firewall table carries `FirewallSyslogUdp` (514/udp),
`FirewallSyslogTcp` (514/tcp), and `FirewallWebHttps` (5443/tcp) — three rules, all present.
`Wix4SecureObject` table carries exactly two ACL grants on the data directory:
`Administrators` and `[SERVICEACCOUNT]` (resolves to
`NT SERVICE\VSoftSol Syslog Manager`), both `GenericAll` (`Permission=268435456`).

MSI SHA-256 (this build):
```
043658c2b4ea9c18611a19a84785f1c6bcf1e69f9aa5bda9e6385e815a3a5cb3
```

## White-label rebranding acceptance test

`branding/brand.json` was temporarily replaced with a different product name
("Acme Log Sentinel"), vendor ("Acme Networks Inc"), and colours; the solution and the
installer were rebuilt with **zero changes to any `.wxs`/`.cs`/`.razor` source file**. The
decompiled MSI showed:
```
<Package Manufacturer="Acme Networks Inc" Name="Acme Log Sentinel" ...>
<Directory Id="INSTALLFOLDER" Name="Acme Log Sentinel">
<ServiceInstall Name="Acme Log Sentinel" DisplayName="Acme Log Sentinel"
                Account="NT SERVICE\Acme Log Sentinel"
                Description="...Part of Acme Log Sentinel." ... />
```
`BrandingInfo.g.cs` (the generated C# constants Web/Core/Data compile against) matched the
new brand identically. `branding/brand.json` was restored to the real product identity
immediately afterward and the solution + installer rebuilt again, confirmed back to
"VSoftSol Syslog Manager" / "Vision Software Solutions" throughout.

To make this test possible with zero installer source changes, `VSoftSol.Syslog.BrandingGen`
(the existing Phase 0 branding pipeline, which already generated `BrandingInfo.g.cs` and
every image asset from `branding/brand.json`) was extended this phase to also generate
`installer/Strings.en-us.wxl` — the installer's own product-name/manufacturer/vendor-URL/
support-email strings were, until this phase, hand-written literals that would have made
the white-label test fail; now they flow from the exact same single source of truth as
every other brand-visible surface. `installer/Strings.en-us.wxl` is gitignored alongside
`BrandingInfo.g.cs` and every other generated branding artifact.

## Backup/restore

See `backup-restore.md` in this evidence folder.
