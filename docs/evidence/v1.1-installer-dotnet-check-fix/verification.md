# v1.1 — installer's .NET-runtime launch condition fixed — verification

## Severity

**High, user-facing, present since `v1.0.0`.** `installer/Product.wxs`'s `<Launch
Condition="Installed OR DOTNETSHAREDHOST" ...>` blocks setup with "requires the ASP.NET
Core Runtime 8.0.x (Hosting Bundle)" unless one of the two properties is true.
`Installed` is a WiX built-in that is only true during a repair/upgrade of an
*already-installed* product — on every first-time install it is empty. `DOTNETSHAREDHOST`
was populated from a `RegistrySearch` reading
`HKLM\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost`, but a 64-bit
Hosting Bundle install registers at the native (non-WOW6432Node-redirected)
`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost` instead. The package itself
is built `Platform=x64` (`Installer.wixproj`), so a `RegistrySearch` with no `Bitness`
override already reads the native 64-bit registry view by default — hard-coding
`WOW6432Node` into the literal key path pointed the search at a location a 64-bit install
never populates.

**Practical effect: every first-time install of the MSI, on every machine, regardless of
whether the correct .NET runtime is actually present, hit this same blocking dialog** — the
installer's own prerequisite check could never pass. `git log --follow` confirms this line
has been unchanged since it was written for `v1.0.0` (commit `d0bdd94`, Phase 12) — this
was never actually caught by a real, interactive `msiexec` run in this sandbox (no clean VM
here; Phase 12's own UI verification scripted the *web app's* first-run wizard via `curl`,
which cannot exercise a native MSI's launch condition at all — that needs a real `msiexec`
process with real registry state, which only a human clicking through the installer can
provide). Found and reported by the user's own real install attempt, on a real machine.

## Reproduction — live, on the reporting machine

```
$ Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost"
NOT FOUND: Cannot find path '...' because it does not exist.

$ Get-ItemProperty -Path "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost"
Version      : 8.0.31
Path         : C:\Program Files\dotnet\
```

The ASP.NET Core 8.0.31 Hosting Bundle was genuinely installed and registered at the
correct, standard location; the installer was checking a path that has never existed on a
64-bit Hosting Bundle install.

## Fix

`installer/Product.wxs` — the `RegistrySearch`'s `Key` changed from
`SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost` to
`SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost`.

## Verification

- `dotnet build installer/Installer.wixproj -c Release` — 0 Warnings, 0 Errors, succeeds.
- No automated test exercises this launch condition: doing so needs a real `msiexec`
  process with real, elevated, machine-wide registry state — not something this repo's
  xUnit suites (or this sandbox, which has no browser/clean VM) can drive, the same class
  of limitation already documented for every other "needs a clean VM/real hardware/human
  clicking through it" item in this project's known-issues history. The live registry
  reproduction above, plus the user re-running the corrected MSI, is the closest available
  substitute for an automated red/green pair.
- The corrected MSI was rebuilt (`dotnet build installer/Installer.wixproj -c Release`,
  fresh output at `installer/bin/x64/Release/en-us/VSoftSolSyslogManagerSetup.msi`) and
  handed to the user to re-run on the same reporting machine.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new bullet in the fresh "Unreleased" section opened after
  `v1.1.0` (v1.0.0's and v1.1.0's own text left untouched).
- `PROGRESS.md` — new v1.1-series log entry describing the bug's real severity plainly
  rather than downplaying it, since every prior "installer verified" claim in this
  project's history (Phase 12's own sign-off included) never actually exercised this code
  path with a real interactive install.
- `docs/evidence/phase-12/known-issues.md` — left untouched, per this project's "never
  rewrite history" convention; the gap this bug represents in that phase's own sign-off is
  stated here instead, in the new work's own record.
