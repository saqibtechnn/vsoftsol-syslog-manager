# CLAUDE.md — VSoftSol Syslog Manager

Claude Code reads this file automatically at the start of every session. It is the
standing contract for this repository. Read it fully before writing any code.

**Then read `START_HERE.md`** — it is the execution controller and tells you which phase
to run and how to run it. Also binding: `TESTING_STANDARDS.md`,
`SECURITY_STANDARDS.md`, `UX_STANDARDS.md`, `BRANDING.md`, and `VENDOR_SUPPORT.md`.

---

## Product

- **Name:** VSoftSol Syslog Manager
- **Vendor:** Vision Software Solutions (vsoftsol.com)
- **Target:** v1.0.0
- **What it is:** On-premises Windows syslog collection and log management application.
  Single node. One installer. No external runtime dependencies.

---

## Commands

Run these from the repository root. If a command fails, fix it before continuing.

```bash
dotnet restore                                    # restore packages
dotnet build --configuration Release              # must be warning-clean
dotnet test                                       # all tests
dotnet test tests/VSoftSol.Syslog.UnitTests       # unit only
dotnet test tests/VSoftSol.Syslog.IntegrationTests # integration only
dotnet format --verify-no-changes                 # style gate
dotnet run --project src/VSoftSol.Syslog.Service  # run collector in console mode
dotnet run --project src/VSoftSol.Syslog.Web      # run UI standalone
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*"
```

Coverage gate:
```bash
dotnet test /p:CollectCoverage=true /p:Threshold=80 /p:ThresholdType=line \
  /p:Include="[VSoftSol.Syslog.Ingestion]*%2c[VSoftSol.Syslog.Rules]*%2c[VSoftSol.Syslog.Reporting]*"
```

---

## Repository layout — do not invent alternatives

```
src/
  VSoftSol.Syslog.Core/          domain models, enums, interfaces, no I/O
  VSoftSol.Syslog.Data/          SQLite, migrations, repository implementations
  VSoftSol.Syslog.Ingestion/     listeners, queues, parsers, extractors
  VSoftSol.Syslog.Rules/         rule evaluation, actions, alert evaluators
  VSoftSol.Syslog.Reporting/     retention, archival, report generation
  VSoftSol.Syslog.Service/       Windows Service host (composition root)
  VSoftSol.Syslog.Web/           Blazor Server UI
tests/
  VSoftSol.Syslog.UnitTests/
  VSoftSol.Syslog.IntegrationTests/
  VSoftSol.Syslog.Benchmarks/
  fixtures/messages/             real-world syslog samples by vendor
installer/                       WiX project
docs/                            admin guide, user guide, sizing guide, ADRs
PROGRESS.md                      current phase state — update at end of every phase
UX_STANDARDS.md                  binding UI rules — read before building any screen
START_HERE.md                    execution controller — session protocol and phase order
BRANDING.md                      asset pipeline spec
VENDOR_SUPPORT.md                device compatibility tiers, parser packs, config commands
TESTING_STANDARDS.md             binding QA contract — taxonomy, evidence, sign-off
SECURITY_STANDARDS.md            binding AppSec contract — threat model, ASVS, gates
docs/security/                   threat model, ASVS checklist, review, pentest report
docs/evidence/phase-NN/          committed proof for each phase's gates
branding/                        operator-supplied logo and brand files (source of truth)
```

Dependency direction is strictly inward: `Web` and `Service` may reference anything;
`Core` references nothing. Never add a reference that points outward from `Core`.

---

## Non-negotiable constraints

1. **Single node.** No clustering, no external message broker, no sharding.
2. **No external runtime dependencies.** No Elasticsearch, MongoDB, Java, Docker, or
   Redis. SQLite only.
3. **Never lose a message.** Ingestion survives writer stalls, disk pressure, and
   hard process kills.
4. **Always keep the raw bytes.** Every message stores `raw_message` verbatim,
   including messages that fail to parse. Any design that discards the original is wrong.
   Corollary: **no message is ever rejected for coming from an unrecognised vendor.**
   Collection is universal by protocol; parsing is per-vendor and user-extensible. See
   `VENDOR_SUPPORT.md`.
5. **Not internet-facing.** UI is HTTPS-only, bound to localhost or a named LAN interface.
6. **Deterministic runtime.** No LLM calls in the running product. Leave the seam for a
   v2 AI Analysis Agent; build nothing for it now.
7. **One configuration surface.** Everything configurable lives in the database and is
   editable from the web UI. There is no second console and no hand-edited config file
   required for normal operation.
8. **The UI must be usable by a network admin with no training.** Setup to first message
   in under ten minutes, with no config-file editing and no documentation lookup. Every
   screen obeys `UX_STANDARDS.md` — read it before building any UI, and run its
   five-point UX gate before committing any phase that ships a screen. A feature that
   works but cannot be found or configured without help is not done.
9. **Every field from the network is hostile input.** This product ingests
   unauthenticated, attacker-controllable data by design and renders it to privileged
   admins. `SECURITY_STANDARDS.md` is binding: parameterized queries only, encode at
   render never at ingest, allow-lists not deny-lists, fail closed. No Critical or High
   finding may exist at any phase tag.
10. **Testing is not optional and not self-assessed.** `TESTING_STANDARDS.md` is
   binding. Every test must be observed failing before it passes. Every phase commits an
   evidence pack to `docs/evidence/phase-NN/`. A claim without committed evidence is not
   a completed gate, and no phase is tagged with a failing sign-off block.
11. **Branding is data, never code.** Product name, vendor name, colours, URLs, and every
   image come from `branding/` and `brand.json` via `BrandingInfo`, per `BRANDING.md`.
   A grep for `"VSoftSol"` under `src/` must return only the generated
   `BrandingInfo.g.cs`. Replacing the logo is a file drop plus a rebuild — never a code
   change. A missing logo produces a build **warning** and a placeholder fallback, never
   a build failure.

---

## Working method — follow this loop every time

1. **Explore.** Read the relevant existing code and the current phase prompt. Do not
   write code yet.
2. **Plan.** State the files you will create or modify and the tests you will write.
   Wait for confirmation on anything ambiguous.
3. **Test first.** Write the failing test before the implementation, for every feature
   and every bug fix.
4. **Code.** Implement until tests pass.
5. **Verify.** Run the exact verification commands listed in the phase prompt. Paste
   the real output. Do not claim a gate passed without showing it.
6. **Commit.** Conventional commit message, then tag the phase.
7. **Update PROGRESS.md** with what shipped, what deferred, and any decision made.

---

## Rules of engagement

- **One phase per session.** Finish the phase, commit, tag, update PROGRESS.md, then
  the operator clears context and opens the next phase prompt.
- **Do not skip ahead.** Never implement a later phase's feature because it seems easy.
- **Do not silently expand scope.** If a requirement is ambiguous, state your
  interpretation in one line and proceed; if it is a genuine fork, ask.
- **No stubs carried forward** without an explicit `// TODO(phase-N):` marker and an
  entry in PROGRESS.md under "Deferred".
- **Boring code wins.** This is infrastructure someone maintains at 2 a.m. Prefer
  readable over clever. No reflection tricks, no premature abstraction.
- **Two seams only.** `ILogRepository` (for future PostgreSQL) and
  `IAuthenticationProvider` (for future AD). Do not add speculative interfaces beyond these.
- **Secrets never in source.** SMTP passwords, webhook tokens, and ODBC strings are
  DPAPI-encrypted at rest and never logged.
- **Never log message payloads at Debug level in production paths** — syslog bodies can
  contain credentials.

---

## Code conventions

- C# 12 / .NET 8 LTS. Nullable reference types enabled. `TreatWarningsAsErrors` on.
- `async`/`await` throughout the ingestion and data paths; no `.Result`, no `.Wait()`.
- `System.Threading.Channels` for in-process queues.
- Serilog for internal logging; structured properties, never string concatenation.
- xUnit + FluentAssertions. Test names: `Method_Scenario_ExpectedResult`.
- One public type per file, filename matches the type.
- All timestamps stored and compared in UTC. Convert to local only at the UI edge.

---

## Definition of Done (applies to every phase)

- [ ] `dotnet build -c Release` is warning-clean
- [ ] `dotnet test` is fully green
- [ ] `dotnet format --verify-no-changes` passes
- [ ] The phase's own verification commands ran, with output pasted
- [ ] Every new test was observed failing before it passed (`red-green.md`)
- [ ] Evidence pack committed to `docs/evidence/phase-NN/`
- [ ] All prior-phase tests still green — no regressions
- [ ] If the phase shipped a screen: the `UX_STANDARDS.md` five-point gate passed, with
      the cold-eyes walkthrough and click count recorded
- [ ] Security gate passed: SAST, SCA, and secrets scan clean; the phase's security
      tests green; evidence in `docs/evidence/phase-NN/security/`
- [ ] Zero Critical or High findings open
- [ ] The `TESTING_STANDARDS.md` §9 sign-off block emitted with **no FAIL lines**
- [ ] PROGRESS.md updated
- [ ] Committed and tagged `v1.0.0-phase.N`
