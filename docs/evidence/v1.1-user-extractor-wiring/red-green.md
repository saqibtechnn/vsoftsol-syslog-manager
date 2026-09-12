# v1.1 — user-authored extractors wired into ingest (P5-3 closed) — red/green

Per `TESTING_STANDARDS.md`: every new test observed failing before it passed.

## 1. `VendorExtractorTests` (unit, new file)

RED — compiled against the pre-change `VendorExtractor` (2-arg constructor only, no
`UserExtractorRegistry` type):

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~VendorExtractorTests"
error CS0246: The type or namespace name 'UserExtractorRegistry' could not be found
error CS1729: 'VendorExtractor' does not contain a constructor that takes 3 arguments
(8 errors total across the 6 new test methods)
```

Reproduced honestly: the implementation was written first, then temporarily `git stash`ed
(the three modified source files) and the new `UserExtractorRegistry.cs` file moved aside,
to compile the new tests against the real pre-change code rather than skipping straight to
green. Restored immediately after observing the failure above.

GREEN — after restoring `UserExtractorRegistry.cs`, `VendorExtractor.cs` (3-arg constructor
+ always-run global stage in `Enrich`), and the DI/composition wiring:

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~VendorExtractorTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 36 ms
```

Covers: no-vendor-match + no-user-extractors (empty result, existing behavior unchanged);
no-vendor-match but a user extractor matches (null vendor, fields still populated — the
actual point of the feature); a vendor pack matches and a user extractor also runs (fields
from both merged); a user-extractor field name colliding with a pack field name (documents
the deliberate last-writer-wins ordering); `UserExtractorRegistry`'s default-empty and
`SetPipeline` behavior.

## 2. `UserExtractorLoaderHostedServiceTests` (integration, new file)

RED — the type does not exist yet:

```
$ dotnet build tests/VSoftSol.Syslog.IntegrationTests -c Release
error CS0246: The type or namespace name 'UserExtractorLoaderHostedService' could not be
found (8 errors across the 4 test methods)
```

GREEN — after adding `UserExtractorLoaderHostedService.cs` and wiring it into
`AddCollectorRuntime`:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~UserExtractorLoaderHostedServiceTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 383 ms
```

Covers: an enabled saved extractor loads and actually extracts on a real message; a disabled
one is skipped; a malformed saved pattern (the store itself never validates regex syntax —
only the tester's live preview does, so a bad one can still land in the table) is logged and
skipped without preventing the other, valid extractors from loading; the empty-store case
leaves the registry with a zero-stage pipeline.

## 3. `MessageParserTests.Parse_NoVendorPackForThisDevice_StillAppliesASavedGlobalUserExtractor`
   (unit, added to the existing file)

Not a RED/GREEN pair in the strict sense — by the time this test was written,
`VendorExtractor`/`UserExtractorRegistry` were already implemented and covered directly
(§1 above). This test instead proves the wiring holds end-to-end through the real
`MessageParser` → `VendorExtractor` path (`ParsingComposition.Build(userExtractors:)`), the
same "no full DI composition root in tests" convention this codebase always uses. It passed
on first run, as expected for a characterization test of already-tested units composed
together — flagged here explicitly rather than silently presented as a RED/GREEN case it
was not:

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~MessageParserTests"
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 243 ms
```
