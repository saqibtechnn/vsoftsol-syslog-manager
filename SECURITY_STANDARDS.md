# SECURITY_STANDARDS.md — Application Security Contract

Binding for every phase, alongside `TESTING_STANDARDS.md`.

This product ingests **unauthenticated, attacker-controllable data from the network by
design** and renders it to privileged administrators. That combination is the reason
security here is a first-class engineering concern rather than a hardening pass at the end.

---

## 1. Baseline and target

- **OWASP ASVS 5.0, Level 2** is the requirements baseline. Every ASVS L2 control that
  applies is either implemented or explicitly marked not-applicable with a reason in
  `docs/security/ASVS-checklist.md`.
- **OWASP Top 10 (2021)** and **OWASP API Security Top 10** are the minimum coverage for
  test design.
- **CWE Top 25** informs the fuzzing and negative-test corpus.

---

## 2. The attack surface unique to a syslog collector

Generic web-app security advice under-serves this product. These are the threats that
actually apply, and each has a named owning phase.

| Threat | Why it matters here | Owner |
|---|---|---|
| **Log injection / forging** | An attacker who can reach any monitored device can inject CRLF sequences to fabricate log entries, forge a different hostname, or split one message into several. This corrupts the audit record the product exists to provide. | Phase 3 |
| **Stored XSS via log payload** | Message bodies are attacker-controlled and rendered to admins in the grid, context view, live tail, exports, and PDFs. Six output surfaces, one bad escape. | Phases 5, 9, 10 |
| **CSV / formula injection** | A message beginning `=`, `+`, `-`, or `@` becomes an executable formula when the exported CSV is opened in Excel on an admin's workstation. | Phase 5 |
| **Source IP spoofing** | UDP syslog is trivially spoofed. An attacker can forge messages appearing to come from a legitimate device, or flood the discovery queue. | Phases 2, 6 |
| **Resource exhaustion** | Unauthenticated UDP means anyone with network reach can flood the collector, fill the disk, or exhaust the queue. Availability is a security property here. | Phase 2 |
| **ReDoS** | Users author regex for extractors, streams, and rules. A catastrophic pattern stalls the ingest path for every device. | Phases 3, 6 |
| **SSRF via webhook action** | A rule action posts to a user-supplied URL from inside the network. | Phase 7 |
| **Command injection via script action** | A rule can execute a program with fields from an attacker-controlled message. | Phase 7 |
| **Path traversal** | Hostnames and app-names from the wire may reach file paths in file-write actions and archive naming. | Phases 7, 10 |
| **Deserialization / XXE** | Config bundle import consumes an untrusted file. | Phase 11 |
| **Privilege escalation and IDOR** | Four roles with stream and device-group scoping across every list, export, and report. | Phases 4, 5, 6, 9, 10 |
| **Audit tampering** | The audit log is the evidence an auditor relies on. It must be append-only in fact, not by convention. | Phase 4 |
| **Archive tampering** | Cold archives are the compliance record. | Phase 10 |

**The governing rule: every field arriving from the network is hostile input at every
point it is used** — displayed, exported, logged, used in a path, used in a query, passed
to a process, or embedded in a document.

---

## 3. Threat model — a Phase 0 deliverable

`docs/security/THREAT_MODEL.md`, using **STRIDE** per trust boundary:

- Network → listener (unauthenticated, hostile)
- Browser → web UI (authenticated, semi-trusted)
- Application → SQLite file (trusted)
- Application → outbound actions: SMTP, webhook, script, syslog forward, ODBC (egress)
- Operator → installer and config bundle import (privileged, but files may be untrusted)

For each boundary: assets, entry points, threats by STRIDE category, existing mitigation,
and residual risk. **Reviewed and updated at Phases 4, 7, and 11** — a threat model
written once and never revisited is documentation, not security.

---

## 4. Testing layers — all automated in CI from Phase 0

| Layer | Tool | Gate |
|---|---|---|
| **SAST** | Roslyn analyzers + Security Code Scan + CodeQL | No High/Critical findings |
| **SCA** | `dotnet list package --vulnerable --include-transitive` + OSV/Trivy | No High/Critical CVEs |
| **SBOM** | CycloneDX, generated per release | Committed artifact |
| **Secrets scanning** | Gitleaks, full history | Zero findings, ever |
| **DAST** | OWASP ZAP baseline per commit, full scan per phase from 4 onward | No High/Medium |
| **Fuzzing** | SharpFuzz / FsCheck on parsers and the query compiler | No crash, hang, or leak |
| **TLS config** | testssl.sh or sslyze against the running UI and TLS listener | A grade, no weak ciphers |
| **Container/host** | Installer ACL and firewall assertions | Phase 12 |

CI **fails** on any gate. Findings are not warnings to triage later.

---

## 5. Secure engineering rules

1. **Parameterized queries only.** String-concatenated SQL is a build failure, enforced
   by an analyzer rule.
2. **Output encoding at the point of rendering**, never at the point of storage. The
   database keeps the raw bytes (Constraint 4); the UI escapes them. Never sanitize on
   ingest — that would destroy the evidence the product exists to preserve.
3. **Allow-lists, not deny-lists**, for script paths, file destinations, and webhook
   schemes.
4. **Least privilege**: the Windows service runs under a dedicated low-privilege account,
   not LocalSystem. The data directory ACL grants that account only.
5. **Secrets** are DPAPI-encrypted at rest, never logged, never in audit diffs, never in
   config bundle exports, never in error messages, never in source or test fixtures.
6. **Fail closed.** An authorization check that errors denies. A scope filter that cannot
   resolve returns nothing.
7. **No debug endpoints, no default credentials, no backdoor account** in any build.
   Asserted by a test.
8. **Security-relevant events are audited**: login success and failure, authorization
   denial, config change, export, restore, secret change, and script execution.
9. **Dependencies are pinned and reviewed.** No floating version ranges.

---

## 6. Vulnerability management

| Severity (CVSS v3.1) | Fix before |
|---|---|
| Critical (9.0+) | Immediately — blocks the current phase tag |
| High (7.0-8.9) | Before the current phase tag |
| Medium (4.0-6.9) | Before v1.0.0 release |
| Low (0.1-3.9) | Documented, scheduled for v1.1 |

**No Critical or High findings may exist at release.** Every accepted Medium or Low
requires a written justification in `docs/security/SECURITY_REVIEW.md` signed off by the
operator, not by Claude Code.

---

## 7. Pre-release penetration test — Phase 12

Executed and documented, not assumed. Minimum scope:

- Unauthenticated attack against every listener: malformed frames, floods, spoofed
  sources, oversized payloads, protocol confusion
- Authenticated attack per role: horizontal and vertical privilege escalation, IDOR
  across streams and device groups, forced browsing, parameter tampering
- Injection sweep: SQL, command, log, CSV formula, template, header, path traversal
- Stored XSS through the full log path into all six output surfaces
- SSRF through the webhook action, including cloud metadata endpoints and internal ranges
- Config bundle import: XXE, zip-slip, deserialization, oversized and malformed bundles
- Session attacks: fixation, hijacking, concurrent sessions, logout invalidation
- Audit tamper attempts through every available path
- Installer and file-system permissions review on a clean install

Output: `docs/security/PENTEST_REPORT.md` with finding, CVSS score, reproduction steps,
and resolution. An unresolved High blocks the release.

---

## 8. Evidence

Every phase commits to `docs/evidence/phase-NN/security/`:

- SAST, SCA, and secrets scan output
- ZAP scan report (phases with a UI)
- The phase's security test results
- Any new or changed threat model entries
- Findings with severity and disposition

**A phase with no security evidence has not completed its security gate**, regardless of
what its functional tests show.
