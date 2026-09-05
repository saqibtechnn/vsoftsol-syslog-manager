# ADR 0006 — Least-privilege service account and data-directory ACLs

**Status:** Accepted (Phase 0), enforced by the installer in Phase 12

## Context

The service ingests unauthenticated network data and executes user-authored rule actions
(scripts, webhooks) in later phases. SECURITY_STANDARDS.md §5.4 requires least privilege;
retrofitting privilege separation late is expensive.

## Decision

- The Windows Service runs as a **dedicated low-privilege virtual account**
  (`NT SERVICE\<ServiceName>`), never `LocalSystem`.
- The **data directory** (database, spill queue, archives, logs) is ACL'd to grant that
  account and Administrators only; no world-writable paths.
- Listener ports: the service binds UDP/TCP sockets on the configured ports; no other
  elevated capability is required for normal operation. Binding ports < 1024 needs no
  special right on Windows.
- Firewall rules for the listener ports are added by the installer and **removed on
  uninstall** (Phase 12).
- Rule actions that run programs (Phase 7) execute as the same low-privilege account,
  with an allow-list of script paths — never elevated.

## Alternatives rejected

- **`LocalSystem`:** simplest, and wrong — a parser or action-engine RCE would be a full
  host compromise.
- **A normal user account with a stored password:** a credential to manage and rotate;
  virtual service accounts avoid the secret entirely.

## Cost accepted

- The installer must create the account and set ACLs correctly on every supported
  Windows Server version (install matrix, Phase 12).
- Operators who point the data directory at a custom location must let the installer
  re-apply ACLs (documented in the Admin Guide).
