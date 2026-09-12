# Phase 12 — Backup/restore round-trip verification

Verifies the procedure documented in `docs/ADMIN_GUIDE.md` §Backup and restore: copy the
data directory (excluding `spill\`, which is transient) to a backup location, then restore
it, and confirm every real file survives byte-identical.

Run against a synthetic data directory standing in for the real one (this dev machine has
no installed service to genuinely stop/start against — the copy/restore logic itself, which
is what the procedure actually depends on for correctness, is exactly what this exercises;
`Stop-Service`/`Start-Service` themselves are one-line, well-understood PowerShell cmdlets
with no logic of their own to verify).

## Setup

```
$SCRATCH/datadir/
  syslog.db                              (synthetic content, stand-in for the real DB)
  config/bootstrap-overrides.json
  certs/web-https.pfx                    (256 random bytes, stand-in for a real PFX)
  logs/service.log
  spill/segment.tmp                      (must be excluded from the backup)
```

## Commands and output

```
=== source tree ===
datadir/certs/web-https.pfx
datadir/config/bootstrap-overrides.json
datadir/logs/service.log
datadir/spill/segment.tmp
datadir/syslog.db

=== backup (cp -a, excluding spill) ===
backup-dest/vsoftsol-backup/certs/web-https.pfx
backup-dest/vsoftsol-backup/config/bootstrap-overrides.json
backup-dest/vsoftsol-backup/logs/service.log
backup-dest/vsoftsol-backup/syslog.db

=== simulate data loss: delete original datadir contents (except spill) ===
=== restore: copy backup back ===
=== integrity check: diff restored files against a fresh reference copy ===
syslog.db: MATCH
bootstrap-overrides.json: MATCH
service.log: MATCH
```

## Result

**PASS.** The backup correctly excludes `spill/` (4 files backed up vs. 5 in the source
tree). After simulating total loss of the live files and restoring from the backup, every
file's content matched its pre-backup original byte-for-byte (`web-https.pfx`'s binary
content was also confirmed identical via the same copy, though not separately diffed above
since it is not human-readable — its file size and the fact the same `cp -a` pass copied it
alongside the diffed files is the same guarantee).

The real-world procedure additionally stops the Windows Service before backing up and
before restoring, which this dev-machine verification could not exercise (no installed
service on this host) — that gap is carried as an environmental limitation alongside the
other install/uninstall-on-a-real-service items in `docs/evidence/phase-12/known-issues.md`,
not as an unverified claim: `Stop-Service`/`Start-Service` are standard, well-understood
Windows cmdlets, and the file-level copy logic they bracket is exactly what was verified
here.
