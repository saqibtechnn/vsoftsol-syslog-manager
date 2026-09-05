# ADR 0004 — Zstandard over gzip for archives

**Status:** Accepted (Phase 0), implemented Phase 10

## Context

The retention tier (Phase 10) compresses cold log segments for long-term, tamper-evident
storage with SHA-256 verification. Logs are highly repetitive text.

## Decision

**Zstandard (zstd)** for archive compression, via a maintained managed/native binding
pinned in `Directory.Packages.props`.

## Alternatives rejected

- **gzip / Deflate (`System.IO.Compression`):** in-box and zero-dependency, but markedly
  worse ratio and slower at equivalent ratio on this kind of data. Kept as the
  interchange format for user-initiated exports only.
- **brotli:** great ratio, slower on large batches; tuned for HTTP payloads, not bulk
  archival.
- **LZ4:** excellent speed, weaker ratio; archival favours ratio since the data is cold.

## Cost accepted

- One native dependency in the archival path. Mitigated: pinned version, SBOM tracked,
  SCA-scanned, and isolated to `Reporting`.
- Archive readers need the same library; the format and version are recorded in the
  archive manifest.
