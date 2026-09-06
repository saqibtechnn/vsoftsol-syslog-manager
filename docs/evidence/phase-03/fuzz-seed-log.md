# Phase 3 — fuzz seed log

TESTING_STANDARDS.md §2.3: all randomness is seeded and the seed is logged.

| Test | Seed / generator | Cases | Determinism |
|---|---|---|---|
| `ParserPropertyTests.Parse_ForAnyByteSequence…` | FsCheck default `Arb.Default.Array<byte>()`, `MaxTest = 12,000`, `EndSize = 4,000` | 12,000 | FsCheck default RNG; failures shrink and print the seed |
| `ParserPropertyTests.Parse_ForAnyPrintableString…` | FsCheck `Arb.Default.String()`, `MaxTest = 10,000` | 10,000 | as above |
| `ParserPropertyTests.Parse_AnyString_CompletesQuickly` | FsCheck `Arb.Default.String()`, `MaxTest = 5,000` | 5,000 | as above |
| `ParserPropertyTests.Parse_TenThousandRandomFrames…` | `new System.Random(20260906)` | 10,000 | fixed seed **20260906** |
| `ParserFuzzTests` header edge cases | `[Theory]` inline data (truncated PRI, `<>`, `<0>`, `Mar`, out-of-range date, …) | 9 | fully deterministic |
| `ParserFuzzTests` 1 MB / nested-SD / 10k-SD / ANSI / NUL | constructed inputs, no RNG | 5 | fully deterministic |
| `ParserFuzzTests.RegexBacktrackingBait…` | fixed evil pattern `(?<x>(.*,){20})z` + fixed hostile input | 1 | fully deterministic |
| `OracleDifferentialTests` | the committed 200-fixture corpus | 150 (those the reference parses) | fully deterministic |
| `WireFuzzTests` (Phase 2, still green) | `new Random(20260906)` | 20,000 fast / 1,000,000 Soak | fixed seed **20260906** |

No fuzz case has produced a crash, hang, unbounded allocation, or a `raw_message` that
differs from the input, across all runs.
