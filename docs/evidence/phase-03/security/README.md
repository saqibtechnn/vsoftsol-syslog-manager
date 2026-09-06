# Phase 3 — Security evidence

SECURITY_STANDARDS.md §8 / PHASE_03 Security Validation. Every field from the network is
hostile input; this phase turns it into structured fields rendered to privileged admins.

## Verified

| Gate | How | Result | Test |
|---|---|---|---|
| **Log injection / forging** | CRLF, embedded newlines, fake `<PRI>` prefixes inside the body, forged hostnames, NUL bytes | Exactly **one** stored event per frame; `framing_anomaly` field set; the injected bytes are retained as *data* in the one event's body, never re-parsed as a second record | `LogForgingSecurityTests.CrlfInjection_ProducesExactlyOneEvent…`, `…MessageWithManyNewlines…`, `…FakePriorityPrefix…` |
| **Source-IP authority** | A message claiming `dc01.corp.local` (or any hostname) from wire IP `203.0.113.66` | `source_ip` = the wire IP, always; the claimed hostname is a separate field | `LogForgingSecurityTests.ForgedHostnameInMessage…`, `MessageParserTests`, `ParsingPipelineTests.WireObservedSourceIp_IsAuthoritative…` |
| **No sanitisation on ingest** | `<script>alert(1)</script>`, `=cmd\|'/c calc'!A1`, `../../../../etc/passwd`, `${jndi:ldap://evil/a}` | Stored **byte-identical** in `raw_message`; explicit passing test so no later phase "fixes" it the wrong way (encoding is at render) | `LogForgingSecurityTests.HostilePayload_IsStoredByteIdentical…` |
| **ReDoS** | Every pack- and user-authorable pattern compiles with a mandatory match timeout (`Parsing:RegexTimeout`, 250 ms). Catastrophic-backtracking bait pattern `(.*,){20}z` fed a hostile string | The timeout fires, `extractor_timeout` is recorded, and extraction continues (< 5 s) | `ParserFuzzTests.RegexBacktrackingBait_HitsTheMatchTimeout…` |
| **Memory exhaustion** | 5,000-key KV message; a single 1 MB field; 10,000 RFC 5424 SD elements | Field count capped at `MaxFieldsPerMessage` (`fields_truncated` set); value clamped to `MaxFieldValueLength`; SD serialised to one bounded `structured_data_json`, not exploded into 10k `event_fields`; all bounded < 5 s | `MemoryBoundsSecurityTests` (4) |
| **Fuzz — the parser never throws / hangs / loses bytes** | FsCheck `Array<byte>` + `String` (≥ 22,000 cases) + 10,000 seeded-random frames + header edge cases + 1 MB / nested-SD / ANSI / NUL | Zero exceptions; always returns an event; `raw_message` byte-identical every time; every case < 2 s | `ParserPropertyTests`, `ParserFuzzTests` |
| **Differential / oracle** | 150 fixtures run through an **independent regex-based reference parser** and compared field-by-field (Docker/WSL unavailable — see `oracle-comparison.md`) | **0 divergences** on PRI→facility/severity, timestamp, hostname, app-name, proc-id, msg-id, message | `OracleDifferentialTests` |
| **Malformed input → raw, never an exception** | Truncated priority, missing timestamp, oversized, invalid UTF-8, empty payload | Every one produces a stored `raw` event linked to the **Parse Failures** stream | `MessageParserTests`, `ParserFuzzTests`, `ParsingPipelineTests` |
| **SCA** | `dotnet list package --vulnerable --include-transitive` | Clean, all 13 projects — **no new dependency** (GROK is a ~120-line in-repo subset) | `sca-vulnerable.txt` |
| **SAST** | Roslyn + SecurityCodeScan as build errors; `TreatWarningsAsErrors` | Warning-clean; the parser builds no SQL, spawns no process, uses no crypto | build output |

## Mutation testing (PHASE_03 Validation)

Stryker.NET config (`stryker-config.json`) is complete and targets the parser + extractor +
pack parser. It **still cannot complete a run on this SDK-only host** (P0-2, documented
since Phase 0 — the VsTest adapter does not deploy). It needs a CI host with the adapter.
Carried as `known-issues` P3-3. The parser's test suite is deep — RFC compliance, 200
committed fixtures with field-by-field assertions, property, fuzz, and the independent
oracle — so mutation coverage is expected to be strong; it is not yet *proven* on this host.

## Threat model

`docs/security/THREAT_MODEL.md` B1 rows for log injection / forging and parser
memory-safety are moved from `planned` to `implemented`. No new residual risks.
