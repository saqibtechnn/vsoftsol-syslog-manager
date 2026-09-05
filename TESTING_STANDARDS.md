# TESTING_STANDARDS.md — QA Contract

Binding for every phase. `CLAUDE.md` Constraint 10 makes this mandatory.

The purpose of this file is to make "it works" mean something specific. A green test run
that was never observed failing proves nothing, and a benchmark quoted from memory is not
a measurement.

---

## 1. Test taxonomy

Every phase produces tests in the categories that apply to it. Categories are not
optional substitutes for one another — integration tests do not replace unit tests.

| Category | What it proves | Where it lives |
|---|---|---|
| **Unit** | One class behaves correctly in isolation | `UnitTests` |
| **Integration** | Components work together against a real SQLite file and real sockets | `IntegrationTests` |
| **Property-based** | An invariant holds across thousands of generated inputs (FsCheck) | `UnitTests` |
| **Fuzz** | Hostile and malformed input never crashes, hangs, or corrupts | `UnitTests` |
| **Differential / oracle** | Output matches an independent reference implementation | `IntegrationTests` |
| **Performance** | Throughput and latency targets are met, measured not assumed | `Benchmarks` |
| **Soak** | Behaviour holds over hours, not seconds — leaks, drift, growth | `IntegrationTests` (opt-in trait) |
| **Chaos / fault injection** | Correct behaviour when a dependency fails mid-operation | `IntegrationTests` |
| **Security** | Authorization, injection, traversal, XSS, secret handling | `IntegrationTests` |
| **Accessibility & UX** | `UX_STANDARDS.md` five-point gate | Manual + automated axe scan |
| **Acceptance** | The user-facing criterion in `BUILD_PLAN.md` is met | Phase 12, documented |

---

## 2. Test quality rules — non-negotiable

1. **Red before green.** Every test must be observed failing for the correct reason
   before the implementation exists. State in the phase output that you saw it fail.
   A test written after the code, that has never been red, is not evidence.
2. **No `Thread.Sleep`, ever.** Time-dependent behaviour uses an injected
   `TimeProvider` / virtual clock. A test that sleeps is a test that will flake.
3. **Deterministic.** Same input, same result, every run, any order, any machine.
   Seed all randomness and log the seed. No dependency on wall-clock date, machine
   locale, timezone, or test execution order.
4. **Isolated.** Every test creates and disposes its own database file and its own port
   allocation. No shared mutable state between tests. Tests must pass under
   `xUnit` parallel execution.
5. **One reason to fail.** A test asserts one behaviour. If the name needs "and", split it.
6. **Named as specification.** `Method_Scenario_ExpectedResult`. A reader who never sees
   the body should understand the requirement.
7. **No assertion-free tests.** A test that only checks "did not throw" must say so in
   its name and must be rare.
8. **Never weaken a test to make it pass.** If a test is wrong, fix it deliberately and
   record why in PROGRESS.md. Silently loosening an assertion is the most damaging thing
   you can do in this repository.
9. **Fixtures are version-controlled data**, never generated at test time from the same
   code path under test.

---

## 3. Coverage and mutation

- Line coverage **≥ 80%** on `Ingestion`, `Rules`, and `Reporting`. Enforced in CI.
- Coverage is a floor, not a goal. **Mutation testing (Stryker.NET) on the parser, the
  rules evaluator, and the retention logic** must reach a **≥ 70% mutation score**.
  Coverage proves lines ran; mutation proves the assertions matter.
- Uncovered code must be either deleted or justified in a one-line comment.

---

## 4. Evidence pack — required for every phase

A phase is not complete until `docs/evidence/phase-NN/` contains:

| File | Contents |
|---|---|
| `test-output.txt` | Full `dotnet test` output, pasted verbatim, including the counts |
| `coverage.xml` + `coverage-summary.txt` | Coverage report for the phase |
| `benchmarks.json` | BenchmarkDotNet output for any performance gate in the phase |
| `red-green.md` | For each new test: the failure message seen before implementation |
| `ux-gate.md` | If the phase shipped a screen: the five-point gate results and click counts |
| `known-issues.md` | Anything found and not fixed, with a reason and a target phase |

**Pasting output into chat is not evidence. It must be committed.** A future session,
an auditor, or a customer must be able to open the repository and see how each claim was
verified.

---

## 5. Regression discipline

- **Every test ever written keeps running in every later phase.** The suite only grows.
- If a later phase breaks an earlier phase's test, that is a regression: stop, fix the
  code, and never adjust the older test to accommodate the newer code without recording
  the decision in PROGRESS.md.
- The **ingest throughput benchmark is re-run in Phases 3, 6, 7, and 12**, because
  parsing, stream routing, and rules all sit in the ingest path and each can quietly
  destroy the 5,000 msg/sec target. Record the number each time; a drop over 10% between
  phases must be investigated before proceeding.

---

## 6. Defect protocol

When a bug is found, in any phase, at any time:

1. Write a failing test that reproduces it **first**.
2. Fix the code.
3. Keep the test permanently.
4. Log it in `docs/evidence/phase-NN/known-issues.md` with root cause in one sentence.

Never fix a bug without a test. A bug found twice is a process failure.

---

## 7. Continuous integration — build this in Phase 0

`.github/workflows/ci.yml` (or the local equivalent) runs on every commit and **fails the
build** on any of:

```
dotnet restore
dotnet build -c Release          # warnings are errors
dotnet format --verify-no-changes
dotnet test                       # all categories except Soak
coverage threshold 80%            # Ingestion, Rules, Reporting
dotnet list package --vulnerable --include-transitive   # must be clean
architecture fitness tests        # Core has no outward references
branding literal test             # only BrandingInfo.g.cs contains the product name
```

Soak tests run on a nightly schedule, not per-commit.

---

## 8. Anti-patterns that will be rejected

- A test that passes whether or not the feature exists
- Asserting on log output instead of on behaviour
- Mocking the thing under test
- `catch { }` in a test to make it green
- Comparing a float with `==`
- A "test" that only prints to console
- Skipping or commenting out a failing test to unblock a commit
- Claiming a gate passed without committed evidence

---

## 9. Phase sign-off

At the end of each phase, output this block and commit it to PROGRESS.md:

```
PHASE N SIGN-OFF
  Tests added:            <unit> unit, <int> integration, <prop> property, <fuzz> fuzz
  Total suite:            <N> tests, <M> passing, 0 skipped
  Red-green observed:     yes / no  (if no, explain)
  Coverage:               <x>% on <projects>
  Mutation score:         <x>%  (parser / rules / retention phases only)
  Performance gates:      <metric>: <measured> vs <target>  PASS/FAIL
  UX gate:                PASS / FAIL / N-A
  Regression:             all prior-phase tests green — yes/no
  Evidence committed:     docs/evidence/phase-NN/
  Known issues:           <count>, listed in known-issues.md
```

Any FAIL blocks the tag. There is no partial completion.
