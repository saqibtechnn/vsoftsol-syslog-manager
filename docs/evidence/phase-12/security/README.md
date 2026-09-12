# Phase 12 — security evidence index

The detailed pre-release checklist lives at `docs/security/PENTEST_REPORT.md` (not under
this evidence folder, so it sits alongside the threat model / ASVS checklist / security
review it is part of and can be found the same way those are). This file is the pointer
`docs/evidence/phase-NN/security/README.md` convention expects.

- **Pentest checklist and self-review methodology**: `docs/security/PENTEST_REPORT.md`
- **Threat model updates**: `docs/security/THREAT_MODEL.md` — B2 gets a first-run-wizard
  addendum; B3's ACL/backup rows move from planned to implemented; B5's installer rows move
  from planned to implemented/partial with carried items named explicitly
- **Security review summary**: `docs/security/SECURITY_REVIEW.md`, "Phase 12 — release"
  section
- **ASVS checklist**: `docs/security/ASVS-checklist.md` — V1.2 and V6.4 close from planned
  to implemented this phase
- **No-backdoor automated assertion**: `NoBackdoorTests` (`tests/VSoftSol.Syslog.
  IntegrationTests/Security/NoBackdoorTests.cs`), scanning the published binaries
- **MSI structural security inspection**: `../verification.md` (decompiled-MSI service
  account, ACL grant, and firewall rule confirmation)
- **Backup/restore integrity**: `../backup-restore.md`
