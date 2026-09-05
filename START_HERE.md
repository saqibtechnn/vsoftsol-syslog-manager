# START_HERE.md — Execution Controller

**Operator:** place this file, `CLAUDE.md`, `UX_STANDARDS.md`, `BRANDING.md`,
`BUILD_PLAN.md`, `PROGRESS.md`, the `phases/` folder, and the `branding/` folder in the
repository root. Then open Claude Code and say:

> Read START_HERE.md and begin.

Claude Code follows the protocol below. Nothing else needs to be pasted.

---

## Your standing instructions, Claude Code

You are building **VSoftSol Syslog Manager v1.0.0** for Vision Software Solutions.
This file is your controller. Follow it exactly, every session.

### On every session start — do this before anything else

1. Read `CLAUDE.md` (constraints, layout, commands, conventions).
2. Read `UX_STANDARDS.md` (binding UI rules).
3. Read `BRANDING.md` and list the contents of `branding/`.
3a. Read `TESTING_STANDARDS.md` and `SECURITY_STANDARDS.md`. Both govern every phase.
3b. If the phase touches parsing, intake, or help content, read `VENDOR_SUPPORT.md`.
4. Read `PROGRESS.md` and identify **the next phase**.
5. Read `phases/PHASE_<NN>_*.md` for that phase — **that phase only**.
6. State in one line: "Resuming at Phase N — <name>. Last tag: <tag>."
7. Run `git status` and `dotnet build -c Release`. If the tree is dirty or the build is
   broken, stop and report before writing any code.

### Then run the phase loop

**Step 1 — Explore.** Read the existing code the phase touches. Write no code yet.

**Step 2 — Plan.** Output a numbered plan listing: files you will create, files you will
modify, and the tests you will write first. If anything in the phase prompt is genuinely
ambiguous, state your interpretation in one line and continue. Only stop and ask if it
is a real fork with no defensible default.

**Step 3 — Test first.** Write the tests named in the phase prompt's "Tests to write
first" section and its Validation section. Run them. **Record the actual failure message
for each in `docs/evidence/phase-NN/red-green.md`.** A test that was never observed red
is not evidence and does not count.

**Step 4 — Code.** Implement until the tests pass. Stay inside the phase. If you find
yourself building something listed under "Do not build in this phase", stop and remove it.

**Step 5 — Verify and validate.** Run the exact commands in the phase's Verification
section **and** everything in its Validation & Evidence and Security Validation
sections. Paste the real output
and **commit it to `docs/evidence/phase-NN/`** per `TESTING_STANDARDS.md` §4. Never
assert a gate passed without committed evidence. If the phase ships a screen, run the
five-point UX gate. Then emit the §9 sign-off block. **Any FAIL line blocks the tag.**

**Step 6 — Commit and tag.**
```bash
git add -A
git commit -m "<type>: phase <N> — <summary>"
git tag v1.0.0-phase.<N>
```

**Step 7 — Update PROGRESS.md.** Fill the phase block: shipped, verification output,
benchmark numbers, UX gate results, decisions made, deferred items, known issues. Update
the "Current state" header at the top.

**Step 8 — Stop.** Print:
> Phase N complete and tagged. Clear context and start a new session for Phase N+1.

**Do not begin the next phase in the same session.** Context bloat is the main cause of
quality collapse in long builds. One phase, one session, always.

---

## Rules that override anything else

- **Never skip a phase**, never work two phases at once, never implement a later phase's
  feature because it looks easy today.
- **Never claim a verification passed without committed evidence.**
- **Never weaken, skip, or comment out a test to get to green.** If a test is wrong, fix
  it deliberately and record why. This is the most damaging thing you can do here.
- **Never tag a phase with a FAIL in its sign-off block.**
- **Never tag a phase with an open Critical or High security finding.**
- **Never sanitize a message on ingest to "fix" an XSS finding.** Encode at render.
  Destroying the raw bytes to make a scanner happy breaks Constraint 4 and destroys the
  evidence the product exists to preserve.
- **Never carry a stub forward** without a `// TODO(phase-N):` marker and a matching line
  in PROGRESS.md under Deferred.
- **Never lose a syslog message and never discard raw bytes.** Constraints 3 and 4 in
  `CLAUDE.md` outrank convenience, performance, and elegance.
- **Never invent a different repository layout** than the one in `CLAUDE.md`.
- **Never hardcode branding.** Read it from `branding/` per `BRANDING.md`.
- If a phase cannot be completed as written, stop, explain precisely what blocks it, and
  propose the smallest change. Do not work around it silently.

---

## Recovery

If a phase went wrong and was already committed:
```bash
git reset --hard v1.0.0-phase.<N-1>
```
Then re-run that phase's prompt with a note describing what failed. Update PROGRESS.md to
reflect the rollback.

If the build is broken at session start, fixing it is the whole session. Do not start new
phase work on a red build.

---

## Phase order

| # | File | Tag on completion |
|---|---|---|
| 0 | `phases/PHASE_00_ARCHITECTURE.md` | `v1.0.0-phase.0` |
| 1 | `phases/PHASE_01_DATA_LAYER.md` | `v1.0.0-phase.1` |
| 2 | `phases/PHASE_02_INGESTION_CORE.md` | `v1.0.0-phase.2` |
| 3 | `phases/PHASE_03_PARSING.md` | `v1.0.0-phase.3` |
| 4 | `phases/PHASE_04_UI_AUTH.md` | `v1.0.0-phase.4` |
| 5 | `phases/PHASE_05_SEARCH.md` | `v1.0.0-phase.5` |
| 6 | `phases/PHASE_06_DEVICES_STREAMS.md` | `v1.0.0-phase.6` |
| 7 | `phases/PHASE_07_RULES_ACTIONS.md` | `v1.0.0-phase.7` |
| 8 | `phases/PHASE_08_ALERTS.md` | `v1.0.0-phase.8` |
| 9 | `phases/PHASE_09_DASHBOARDS.md` | `v1.0.0-phase.9` |
| 10 | `phases/PHASE_10_RETENTION_REPORTS.md` | `v1.0.0-phase.10` |
| 11 | `phases/PHASE_11_HARDENING.md` | `v1.0.0-phase.11` |
| 12 | `phases/PHASE_12_RELEASE.md` | `v1.0.0` |

Begin at the phase named in `PROGRESS.md`. If PROGRESS.md says "not started", begin at
Phase 0.
