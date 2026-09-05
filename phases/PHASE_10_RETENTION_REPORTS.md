# PHASE 10 — Retention, Archival & Reports

## Context
Without this phase the database grows until the disk fills. With it, the product has a
compliance story — which is what turns it from a tool into something an auditor accepts.

## Objective
Tiered retention with verifiable archives, and a scheduled report engine.

## Build
1. **Retention tiers, configurable per stream**:
   - *Hot* — uncompressed, fully indexed, searchable. Default 30 days.
   - *Warm* — Zstd-compressed message bodies, still searchable (slower). Default 90 days.
   - *Cold* — exported to compressed archive files on local disk or a UNC path, removed
     from the live database. Default 12 months.
   - *Delete* — after the configured horizon.
2. **Tiering jobs** run on a schedule, in bounded batches, never blocking the writer for
   more than 100 ms per batch. Progress is resumable after a restart.
3. **Zstd compression** via `ZstdSharp`, with gzip fallback if Zstd is unavailable.
   Compression level configurable.
4. **Archive integrity** — every archive file gets a SHA-256 hash recorded in the
   database at creation. A scheduled verification job re-hashes and raises an alert on
   mismatch. This is the tamper-evidence story; do not skip it.
5. **Archive restore** — select an archive, restore into a searchable temporary index,
   auto-expire the restore after N days. Restores are audited.
6. **Report engine**: a report is a saved search + aggregation + template + schedule.
   - Canned templates: Failed Authentication Summary, Configuration Change Audit,
     Device Availability, Interface Flap Report, Severity Trend, Top Talkers,
     Rule & Alert Activity
   - Compliance templates mapped to PCI-DSS, HIPAA, ISO 27001, and SOX logging controls.
     Each states the control it evidences.
   - Custom reports from any saved search
7. **Output**: PDF (QuestPDF) and CSV. Include generation timestamp, time range covered,
   the query used, the generating user, and whether archived data was included.
   PDFs carry the branding from `branding/` — wide logo in the header, copyright in the
   footer, `primaryColor` for headings — all via `BrandingInfo`, no literals. CSV and
   JSON exports carry product name and version in their metadata header.
8. **Scheduling**: daily / weekly / monthly, delivered by email or written to a folder.
   Failed deliveries retry and raise an alert.
9. Reports must be runnable by the **Auditor** role without Administrator help.

## Do not build in this phase
Installer, TLS listener, SNMP. Those are Phases 11 and 12.

## Tests to write first
- **Full lifecycle test**: seed events → age them → hot→warm→cold transitions → verify
  searchability at each tier → archive → verify hash → restore → confirm data identical
  → expire restore.
- Tamper test: modify an archive file on disk, run verification, assert an alert fires.
- Warm-tier search test: compressed rows return the same results as uncompressed.
- Report test: generate each canned template against fixture data, assert the PDF
  renders and the CSV row count matches the underlying query.
- Auditor-role test: an Auditor generates a compliance report end to end with no
  Administrator action.
- Interruption test: kill mid-tiering, restart, assert no data loss and no duplicates.

## Verification — run these and paste output
```bash
dotnet test --filter "Retention|Archive|Report"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Retention*"
```
Confirm tiering a 10M-event backlog does not push search latency past the Phase 5 target
while it runs.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task, performed as an **Auditor-role** user: **generate a
90-day PCI-DSS report and schedule it monthly by email.** Retention configuration must
show the projected disk usage for the chosen settings before saving — never let a user
configure retention blind and discover the consequence when the disk fills. Report
templates are pick-and-run, not build-from-scratch.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **End-to-end data integrity** — checksum every event before tiering; after hot → warm →
  cold → archive → restore, assert **byte-identical** recovery for 100% of events. Any
  loss or mutation is a phase failure, not a known issue.
- **Tamper detection** — modify one byte in an archive, truncate another, replace a third
  with a valid archive from a different period. All three must be detected and alerted.
- **Interruption matrix** — kill during warm compression, during cold export, during
  delete, and during restore. Restart each time; assert no data loss, no duplicates, and
  no half-tiered rows.
- **Report content validation** — extract text from generated PDFs and assert the figures
  match an independent SQL query. A PDF that renders is not a PDF that is correct.
- **Compliance mapping review** — each compliance template states the control it evidences;
  verify the query actually satisfies that control rather than merely being named after it.
- **Auditor-role end-to-end** — the full report task performed with Auditor permissions
  only, proving no hidden Administrator dependency.
- **Mutation testing** on retention logic, **≥ 70%**.
- **Evidence:** integrity checksum report (100% match), tamper matrix (3/3 detected),
  interruption matrix, PDF-vs-SQL figure comparison.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Archive path traversal / zip-slip** — hostnames, stream names, and dates feed archive
  filenames. Test `../`, absolute paths, UNC, and reserved names. On restore, assert
  entries cannot escape the target directory.
- **Malicious archive import** — a crafted archive with traversal entries, a decompression
  bomb, and a mismatched hash. All three must be refused before any extraction begins.
- **Tamper-evidence review** — confirm SHA-256 is computed over the complete archive
  content, that the hash is stored where an attacker with file-system access cannot
  trivially update it in step, and document that limitation honestly in the compliance
  documentation.
- **XSS in PDF and CSV** — the sixth and most-forgotten output surfaces. Assert log
  payloads containing markup and formulas are neutralised in generated PDFs and exported
  CSVs while remaining byte-identical in storage.
- **Report scope enforcement** — an Auditor's report must not include out-of-scope data,
  including through archived and restored data paths.
- **Evidence:** traversal matrix, malicious-archive refusal log, PDF/CSV injection
  results, scope enforcement on reports.

## Definition of Done
Standard DoD, plus the full lifecycle test and the tamper test both pass, and the
Auditor completes the report task unaided.

## Commit
`feat: phase 10 — retention tiering, zstd archives, sha-256 verification, reports` → tag `v1.0.0-phase.10`
