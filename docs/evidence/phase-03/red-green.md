# Phase 3 — red-before-green evidence

TESTING_STANDARDS.md §2.1. The parsers, the extractor pipeline, and the vendor packs were
built test-first: the fixture corpus and the RFC-compliance tests were written against the
canonical schema before the parser existed, and each was observed failing (empty parser →
every assertion red) before implementation.

## Observed red, then green

| Test group | Failure observed before the implementation | Now |
|---|---|---|
| `Rfc5424ParserTests` (13) | `TryParse` returned `false` for every well-formed RFC 5424 message | green |
| `Rfc3164ParserTests` (13) | `TryParse` returned `false` for every BSD-format message | green |
| `YearRolloverTests` (4) | Dec-in-Jan / Jan-in-Dec messages got the receipt year | green |
| `PayloadDecoderTests` (6) | BOM not stripped; invalid UTF-8 threw instead of latin-1 fallback | green |
| `MessageParserTests` (17) | facade returned raw for everything; `source_ip` came from the message hostname | green |
| `ExtractionTests` (14) | GROK / KV / JSON / CSV / lookup / transform each produced no fields | green |
| `VendorFixtureTests` (200 + corpus check) | 0 fields extracted; `vendor` null; parse_status wrong for every fixture | green |
| `DeduplicationWindowTests` (5) | `LookupRecent` always null; no occurrence folding | green |
| `LogForgingSecurityTests` (7) | CRLF-injected text produced no `framing_anomaly`; hostname overrode `source_ip` | green |
| `MemoryBoundsSecurityTests` (4) | field / value / SD caps not enforced | green |
| `ParserFuzzTests` (14) | header edge cases and 1 MB / nested-SD / ANSI / NUL inputs threw | green |
| `OracleDifferentialTests` | production disagreed with the independent regex reference on header fields | green (150 fixtures compared, 0 divergences) |
| `ParsingPipelineTests` (5, integration) | parsed events had no vendor fields; raw events not linked to "Parse Failures"; dedup did not fold | green |

## Defects found and fixed during the phase (defect protocol, §6)

| # | Defect | Found by | Fix |
|---|---|---|---|
| D3-1 | RFC 3164 timestamp scan swallowed the trailing `:` of Cisco's `15.003:`, losing the fractional seconds | `Rfc3164ParserTests.TryParse_CiscoIosSequenceNumberAndSubsecondTimestamp` (red) | trim trailing non-digits from the time slice |
| D3-2 | Cisco `%FACILITY-…` and FortiGate `key=value` first tokens were consumed as a BSD hostname/tag | fixture corpus (cisco-ios / fortigate lines) | `%`-prefixed and `key=` tokens are neither host nor tag — straight to the message |
| D3-3 | The optional `Mmm dd HH:MM:SS UTC:` timezone token ate a short pure-letter hostname (`USG`) | oracle differential + ubiquiti fixtures | require the trailing `:` on the timezone token |
| D3-4 | Cisco ASA `Mmm dd yyyy HH:MM:SS` (with a year) failed the BSD timestamp parse | cisco-asa fixtures (red) | optional 4-digit year between day and time |
| D3-5 | A Palo Alto CSV's embedded `10:00:00` made the second token look like a `tag:` | paloalto fixtures | a tag token may not contain a comma; the name before `:`/`[` must start with a letter |
| D3-6 | `fields_truncated` / `extractor_timeout` could not be set once the field cap was hit | `MemoryBoundsSecurityTests` / `ParserFuzzTests` (red) | `ExtractionContext.SetMeta` bypasses the cap for pipeline meta fields |

Every defect has a permanent regression test.
