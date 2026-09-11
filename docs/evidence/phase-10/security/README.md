# Phase 10 — security evidence

Per SECURITY_STANDARDS.md §8. See `sca-vulnerable.txt` for the SCA run and
`THREAT_MODEL.md`'s Phase 10 addendum / `ASVS-checklist.md`'s Phase 10 pass /
`SECURITY_REVIEW.md`'s Phase 10 section for the narrative. This file indexes the tests
that back each claim.

## Archive tampering (STRIDE: T)

Tamper-evidence is the compliance story this phase exists to deliver. SHA-256 is
computed over the complete archive file at creation, stored in the database (not beside
the file). A scheduled `SqliteArchiveVerifier` pass re-hashes every archive that has gone
stale; `RestoreArchiveAsync` re-verifies before any parsing and refuses a mismatch
outright, marking the archive `tamper_detected`.

- `ArchiveTamperMatrixTests.VerifyDueBatchAsync_DetectsAllThreeTamperKinds` — bit-flip,
  truncation, and swapping in a valid archive from a *different* period, **3/3 detected**;
  a fourth, untouched archive in the same batch verifies `Ok` (no false positive).
- `ArchiveTamperMatrixTests.VerifyDueBatchAsync_ADeletedFile_IsReportedMissing…` — a
  vanished file is reported `Missing`, not silently skipped.
- `RestoreTests.RestoreArchiveAsync_WithATamperedFile_RefusesBeforeInsertingAnything` — a
  tampered archive inserts **zero** rows, not a partial set.

## Path traversal / zip-slip in archive naming (STRIDE: E)

- `ArchiveNamingTests` (unit) — the sanitize/traversal/root-containment matrix.
- `RetentionSecurityTests.ExportColdBatchAsync_WithAPathTraversalStreamName_NeverEscapesTheArchiveRoot`
  — a stream literally named `../../../../windows/system32/evil` produces an archive whose
  resolved path still starts with the configured archive root.

## Malicious archive import — decompression bomb (STRIDE: D)

- `CompressionTests.Decompress_BeyondTheCap_ThrowsInsteadOfMaterialisingTheBomb` (unit,
  both Zstd and Gzip) — a bounded streaming copy refuses before the bomb is materialised.
- `RetentionSecurityTests.ArchiveFile_Parse_RefusesADecompressionBomb_BeforeAllocatingItsFullSize`
  — a real 50 MB-plaintext fixture, compressed to under 1 MB, refused against a 1 MB cap
  through the exact pipeline `RestoreArchiveAsync` uses.
- `RetentionSecurityTests.RestoreArchiveAsync_ChecksTheHash_BeforeCallingArchiveFileParse` —
  proves the hash check runs *before* any parsing (a corrupted-but-hash-checked file throws
  the hash-mismatch exception, not a JSON/format error, which would only be possible if
  `Parse` had already run).

## Cross-scope report leak, including through the restore path (STRIDE: I)

Reports resolve through the same Phase 5/9 scoped readers dashboards already proved
scope-safe — no bespoke query path was written for this phase.

- `RetentionSecurityTests.Report_ArchivedPeriodsOmitted_IsScopedToVisibleStreams_NotAllArchives`
  — the "archived data omitted" disclosure itself does not leak the existence of
  out-of-scope archived data (1 disclosed period for a 2-archive, 1-visible-stream setup).
- `RetentionSecurityTests.RestoredEvents_StayScoped_AnOutOfScopeViewerCannotSeeThemEitherViaSearchOrReports`
  — a restored event is invisible via `GetByIdAsync` and `SearchAsync` to a viewer outside
  its stream's scope, and visible to one inside it (both directions asserted, no IDOR via
  `GetByIdAsync` returning null rather than "forbidden").
- `ReportStoreTests` IDOR set — `GetAsync`/`UpdateAsync`/`DeleteAsync` by a non-owner all
  fail without an existence oracle; a system template is never editable/deletable.

## PDF/CSV injection — the 6th/7th output surfaces (STRIDE: T)

- `ReportRenderingTests.ReportPdfRenderer_Render_WithHostileLogContent_DoesNotThrow_AndStaysLiteralText`
  — `<script>`, a CSV-formula prefix, a NUL byte, and CRLF all render without throwing
  (QuestPDF's `Text()` API draws literal glyphs — no markup-interpretation path exists).
- `ReportCsvWriter_WriteAsync_NeutralisesFormulaInjection_InTheMessageColumn` (4 formula
  prefixes: `=`, `+`, `-`, `@`) — every one is guarded, matching the Phase 5
  `CsvFormulaGuard` precedent exactly (the same class, reused, not reimplemented).

## Full lifecycle + interruption safety

- `RetentionFullLifecycleTests` — one continuous scenario: seed → Hot (searchable) → age →
  Warm (compressed, still searchable, byte-identical) → age → Cold (archived, hash
  verified) → restore (byte-identical to the original) → auto-expire. Every stage asserted.
- `SqliteRetentionEngineTests.Retention_ResumesCleanlyAndWithoutDuplicates_AfterASimulatedCrashBetweenArchiveWriteAndEventDelete`
  — a simulated crash between the archive commit and the event delete resumes on retry
  with no duplicate archive row and no data loss.

## A genuine bug found live during this phase, not by review

A self-deadlock (a non-reentrant write-lock semaphore acquired, then acquired again before
the first acquisition released, across two `await using` scopes in `RestoreArchiveAsync`
and `ExpireRestoresBatchAsync`) hung the first full integration run indefinitely. Found via
process-age inspection, root-caused, and fixed with an explicit block scoping the lock's
lifetime. Documented in full, with the fix, in `known-issues.md` (B10-1) per
TESTING_STANDARDS.md §6 — this is exactly the class of defect the "observed RED for the
correct reason, then GREEN" discipline exists to catch, and it did.
