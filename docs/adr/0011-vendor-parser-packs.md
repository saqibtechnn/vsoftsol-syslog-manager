# ADR 0011 — Vendor parser packs: runtime-loaded plain text

**Status:** Accepted (Phase 3)

## Context

CLAUDE.md Constraint 4: every message is stored, including from an unrecognised vendor —
"parsing is per-vendor and user-extensible" (VENDOR_SUPPORT.md). PHASE_03 item 7: parser
packs must be "plain text files loaded at runtime, not compiled in — adding a vendor must
be a file drop, never a release." The Phase 5 pattern tester will let operators author
packs from the UI.

## Decision

Vendor packs live in `src/VSoftSol.Syslog.Ingestion/Patterns/<vendor>/<vendor>.pack`, are
**plain text**, are copied to the host's output directory (and flow to every referencing
project), and are **loaded and compiled at runtime** by `PatternPackLoader` from
`<base directory>/Patterns` (overridable via `Parsing:PatternsDirectory`).

### Pack format

INI-like. `[pack]` (vendor, priority), `[match]` (`<field> ~ <regex>` recognition rules,
OR-combined), then ordered extractor stages — `[grok]` / `[regex]` (both accept
`%{PATTERN:field}` GROK tokens), `[kv]`, `[json]`, `[csv]` (with per-log-type `when`
guards, for Palo Alto), `[lookup]`, `[transform]` (rename / drop / set).

### Robustness

- A **malformed pack is logged and skipped**, never fatal — the collector always starts.
- Recognition is **first-match-wins by ascending priority**; a message that matches no
  pack keeps its RFC-parsed header and no vendor fields (still fully stored/searchable).
- **The wire-observed `source_ip` is never overwritten** by anything a pack extracts or by
  a hostname claimed in the message.

### ReDoS / resource limits (PHASE_03 Security Validation)

- Every pattern compiles with a **mandatory match timeout** (`Parsing:RegexTimeout`,
  default 250 ms); a timeout is caught, recorded as `extractor_timeout`, and extraction
  continues.
- Field count and value length are capped (`MaxFieldsPerMessage`, `MaxFieldValueLength`);
  overflow sets `fields_truncated`.
- Structured data is serialised to one bounded `structured_data_json` string, never
  exploded into thousands of `event_fields`.
- `MaxMessageChars` caps the decoded text; the raw bytes are always kept in full.

### The core eight

Shipped in Phase 3: `cisco-ios`, `cisco-asa`, `fortigate` (KV), `paloalto` (positional
CSV per log type), `juniper-junos`, `mikrotik-routeros`, `ubiquiti-unifi`, `linux`
(sshd / sudo / cron / kernel). Each has ≥ 25 fixtures with expected output
(`tests/fixtures/messages/<vendor>/corpus.jsonl`), 200 total. The extended seven and
CEF/LEEF are Phase 11.

## Alternatives rejected

- **Compiled-in patterns (C# per vendor):** adding a vendor would need a release — directly
  contradicts the constraint.
- **A third-party grok library (e.g. a NuGet grok):** an added dependency on a
  security-sensitive ingest path, and the SCA gate; a ~120-line GROK subset covers the
  core eight.
- **`RegexOptions.NonBacktracking` for all pack patterns:** linear-time and ReDoS-proof,
  but measured ~40 % slower on the (fast-matching) common case than `Compiled` + timeout.
  Used only as the fallback for the rare pattern `Compiled` cannot express.

## Consequences

- Vendor extraction sits on the ingest hot path. Measured worst case (200k messages all
  matching the busiest pack, on the 2-vCPU dev VM): ~5,100–5,900 msg/sec end-to-end —
  clears the 5,000 gate, marginally. Pure parse+extract is ~105,000 msg/sec; the gap is a
  documented investigation item (`known-issues` P3-2) and re-verified on Phase 12
  hardware. `Parsing:VendorExtractionEnabled = false` keeps RFC-header parsing only
  (~13,600 msg/sec).
- Phase 5's pattern tester compiles user packs through the same `PatternPackParser` +
  `GrokLibrary`, inheriting the timeout and field caps.
