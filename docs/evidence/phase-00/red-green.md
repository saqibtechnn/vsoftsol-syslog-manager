# Phase 0 — Red → Green record

TESTING_STANDARDS.md §2.1: every test observed failing for the correct reason before the
implementation existed. Phase 0 has two kinds of "test": the xUnit suite, and the CI
gates themselves (which must be shown to fail on violation).

## xUnit tests

| Test | Observed RED (reason) | GREEN after |
|---|---|---|
| `SmokeTests.Harness_Runs_True` (Unit) | Ran against an empty project before `SmokeTests.cs` existed → no tests discovered; then temporarily asserted `false.Should().BeTrue()` → `Expected boolean to be True, but found False` (see deliberate-failures.txt) | Restored assertion |
| `SmokeTests.Harness_Runs_True` (Integration) | same | — |
| `CoreArchitectureTests.Core_HasNoDependencyOn_SystemDataOrSystemNet` | Added `file static class OutwardRefDemo` using `System.Net.IPAddress` to Core → `Expected boolean to be True because Core does no I/O. Offenders: …OutwardRefDemo, but found False` (deliberate-failures.txt) | Removed the outward reference |
| `CoreArchitectureTests.Core_HasNoDependencyOn_AnyOtherProductAssembly` | Written before any layer marker referenced Core; verified it passes with a clean graph and fails when an outward ref is added (same mechanism as above) | — |
| `CoreArchitectureTests.Core_ReferencedAssemblies_AreFrameworkOnly` | Would fail if the `BrandingGen` `ReferenceOutputAssembly="false"` reference leaked into IL — confirmed it stays green, proving no IL dependency | — |
| `LayeringTests.*` | Confirmed green; each layer marker deliberately references a `Core` type so the positive "references Core" assertion is meaningful, and NetArchTest-style checks fail if a cross-layer ref is added | — |
| `BrandingLiteralTests.ProductSource_ContainsNoHardcodedBrandStrings…` | (a) initial run flagged `app.css` `#0F4C81` fallback and `wwwroot/branding/brand.css` — both real RED, fixed by removing the hardcoded fallback and excluding generated output; (b) deliberate `<span hidden>Vision Software Solutions</span>` in `Home.razor` → `found at least one item {"…Home.razor :: "Vision Software Solutions""}` (deliberate-failures.txt) | Removed literal; test green |
| `BrandingLiteralTests.GeneratedBrandingInfo_Exists_AfterBuild` | RED before the branding pipeline generated the file | Pipeline generates it before Core compiles |
| `BrandingPipelineTests.Generate_WithOperatorLogo_EmitsEveryDerivedAssetAtExpectedDimensions` | RED before `BrandingGenerator` existed (type not found), then RED while ICO frame parsing was wrong | Implemented generator + ICONDIR writer |
| `BrandingPipelineTests.Generate_WithMissingLogo_WarnsUsesPlaceholderAndStillSucceeds` | RED before the placeholder-fallback branch existed | Implemented fallback + warning list |
| `BrandingPipelineTests.Generate_RunTwice_ProducesByteIdenticalOutput` | RED before the write-if-different guard (PNG re-encode timestamps differed on the first cut) | Content-compare before every write |
| `BrandingPipelineTests.Generate_GeneratedInfo_CarriesBrandValuesFromBrandJson` / `…FallsBackToDocumentedDefaults` | RED before `brand.json` parsing / defaults existed | Implemented flat-JSON parse + defaults |
| `SyslogPriorityTests.*` (4) | RED before `SyslogPriority` existed | Implemented value object |
| `SyslogPriorityPropertyTests.*` (FsCheck, 2) | RED before `SyslogPriority` existed; FsCheck generated facility/severity pairs | Implemented; round-trip + range invariants hold |
| `WebHostSmokeTests.Root_ReturnsPlaceholderPage` | RED with `ObjectDisposedException: TestServer` (a sibling test disposed the shared fixture) — a real defect in the test, fixed | Removed the erroneous `using` on the shared fixture |
| `WebHostSmokeTests.Root_SendsBaselineSecurityHeaders` | RED before `SecurityHeadersMiddleware` was wired | Middleware added to the pipeline |
| `WebHostSmokeTests.Host_BuildsWithoutResolutionErrors` | RED before `AddSyslogPlatform` existed | Composition root implemented |

## CI gates (shown to fail — docs/evidence/phase-00/deliberate-failures.txt)

| Gate | Break introduced | Result |
|---|---|---|
| Warnings-as-errors | `int unusedWarningDemo = 42;` in `CollectorHostedService` | `error CS0219 … Build FAILED` |
| Architecture fitness | outward `System.Net` use in `Core` | arch test FAIL |
| Branding literal guard | hardcoded `"Vision Software Solutions"` in `Home.razor` | literal test FAIL |
| Test harness | `false.Should().BeTrue()` in the smoke test | smoke test FAIL |

All four breaks were reverted; the suite is green and the build is warning-clean
(see test-output.txt).
