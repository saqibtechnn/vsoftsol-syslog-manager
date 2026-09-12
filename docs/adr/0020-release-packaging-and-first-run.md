# ADR 0020 — Release packaging: single-service installer, bootstrap-tier config overrides, and the first-run wizard

**Status:** Accepted (Phase 12)

## Context

The final phase packages everything built in Phases 0–11 into an installable product: a
signed MSI, a first-run experience that gets a network admin from installer to first
message in under ten minutes unaided, and the documentation to support it. Several
decisions here interact with earlier, binding architecture (ADR 0005's single-service
decision, ADR 0006's least-privilege service account, CLAUDE.md Constraint 7's "one
configuration surface").

## Decision 1 — the installer packages and registers `VSoftSol.Syslog.Web.exe` alone

Per ADR 0005 ("One Windows Service hosting collector and UI, not split processes"), exactly
one Windows Service is installed. `VSoftSol.Syslog.Web.exe` is that entry point: it already
hosts Kestrel/Blazor, and now conditionally also calls `AddCollectorRuntime` (the listeners,
ingest pipeline, rules/alerts/retention hosted services) when
`CollectorOptions.HostCollectorRuntime` is true. That flag defaults false — set true only by
`appsettings.Production.json`, which a plain `dotnet run --project src/VSoftSol.Syslog.Web`
never loads (no `ASPNETCORE_ENVIRONMENT` is set in that dev command, so it defaults to
`Development`), preserving the documented "run UI standalone" dev command unchanged. A
Windows-Service-hosted process has no `ASPNETCORE_ENVIRONMENT` set either, so it defaults to
`Production` and picks the file up automatically — no new configuration-layering mechanism
needed, just the framework's own existing environment-based convention.

`VSoftSol.Syslog.Service.exe` (the collector-only console-mode host, unchanged) remains
buildable and runnable by hand for development, but the installer never ships or registers
it — it cannot be the single production entry point without either creating a circular
project reference (Service would need to reference Web to host its Blazor/Kestrel pipeline,
but Web already references Service for the composition root) or duplicating the composition
root, both worse than the conditional-registration approach taken here.

**An earlier draft of the installer packaged two services, one per executable**, before
this ADR was found and re-read during Phase 12 work. Caught before any tag; corrected; the
full regression suite re-run to confirm no behavioural change from the merge. See
`docs/evidence/phase-12/known-issues.md`.

## Decision 2 — bootstrap-tier settings (listener ports) live in a JSON override file in the data directory, not the database

CLAUDE.md Constraint 7 says every setting lives in the database and is UI-editable — but
listener ports (like `CollectorOptions.DataDirectory` before them) must be known **before**
the database is even opened, so they cannot themselves live in it. The first-run wizard's
ports step writes `<data directory>/config/bootstrap-overrides.json`, layered over the
compiled-in `appsettings.json` defaults via `BootstrapConfigOverrides.Apply` before either
host builds its configuration. The data directory is exactly where both the wizard (running
inside the single production process) and a hand-edit (an administrator, documented in the
Admin Guide) already have full-control access from the installer's own ACL grant — no new
permission surface, and this is not the "hand-edited config file for normal operation"
Constraint 7 rules out, since the wizard is what writes it.

**Alternative rejected**: writing directly into the installed `appsettings.json` under
Program Files. Program Files is not writable by the least-privilege service account by
design (ADR 0006) — writing there would mean either weakening that account's privileges or
running the wizard step with elevation, both worse than a bootstrap file in the
already-writable data directory.

## Decision 3 — the wizard is one static-SSR page with server-side, in-memory step state

Each of the five steps is its own plain `EditForm` (or, for the one step with no fields, a
plain `<form>` — an `EditForm` bound to a zero-property model throws at render time, a
genuine framework limitation found while building this) sharing one page, gated by a
`FirstRunWizardState` singleton's `Step` property rather than by URL routing or hidden
carry-forward form fields. This works because setup is inherently a single-occurrence,
single-actor, sequential flow — there is never more than one legitimate wizard session
system-wide, so there is nothing to key per-session state by. The admin's password is held
in that same singleton only from step 1 to step 5 (finish), never written to disk until
hashed, and cleared immediately after use.

**Alternative rejected**: an interactive-server (`@rendermode InteractiveServer`) wizard
with client-side state. Every other static-SSR form page in the product (`Login.razor`)
already establishes the working pattern for this kind of flow, and introducing interactive
rendering into what is otherwise a five-screen linear form would be new machinery for no
behavioural benefit.

## Decision 4 — "first run" is install-wide, not tied to one username

`FirstRunState.IsFirstRunAsync` returns true only while **no** user account anywhere has a
password set, rather than checking specifically the seeded `admin` account. This matters
for a reason beyond correctness-in-the-abstract: nearly every pre-existing integration test
seeds its own differently-named test user and never touches the built-in `admin` row at
all — a username-specific check would have sent the entire pre-existing test suite into an
unintended first-run redirect the moment the gate was wired in. The install-wide
definition is also the more honest one: "has this install ever been configured," not "has
this one specific account."

## Consequences

- Two pre-existing, phase-spanning architecture-fitness tests
  (`ScopeChokepointArchitectureTests`, `AuthorizationMatrixTests`) caught real mistakes in
  this phase's own new code before they shipped — see `docs/evidence/phase-12/red-green.md`.
  Their existence justified itself again.
- `SyslogWebApplicationFactory` (the shared test harness nearly every integration test
  depends on) gained a `SeedCompletedSetup` property, default true, so the dozens of
  pre-existing tests that assume a working credential already exists keep working
  unchanged; only the two first-run-specific test files opt out of that default.
- Listener-port changes after setup need a manual service restart (documented, not live) —
  making them live would need either hot-rebindable sockets or granting the service account
  rights to restart the Windows Service itself, both real features deserving their own
  design, not a rushed addition to a packaging phase.
