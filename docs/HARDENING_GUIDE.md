# VSoftSol Syslog Manager — Hardening Guide

This product ingests unauthenticated, attacker-controllable data by design (every field
from the network is hostile input — `SECURITY_STANDARDS.md`) and renders it to privileged
admins. This guide is for hardening the server it runs on and the network it sits in beyond
the installer's own defaults, which are already: a dedicated least-privilege service
account, a data directory ACLed to that account only, HTTPS-only UI, and firewall rules
scoped to exactly the configured ports.

## Network placement

- **Not internet-facing** (Constraint 5). Place this server on an internal management
  network or VLAN, reachable only by the devices that need to send it syslog and the admins
  who need to reach the dashboard. Do not port-forward the HTTPS or syslog ports to the
  public internet under any circumstance.
- Restrict inbound UDP/TCP 514 (or whatever you configured) to the specific device subnets
  that actually send logs, and the HTTPS admin port to an admin subnet only, at your
  perimeter or host firewall — the installer's own Windows Firewall rules are scoped to
  ports, not source subnets; add that restriction at the network layer (a VLAN ACL, or a
  narrower Windows Firewall rule scope than the installer's default "any").
- Prefer **TCP or TLS** syslog transport over plain UDP for anything carrying sensitive log
  content across a network segment you do not fully trust — UDP syslog has no
  confidentiality or delivery guarantee at the protocol level. The TLS listener (Settings →
  Listeners) is available for devices that support it.

## Service account

Covered in the Admin Guide's Service account section — summarized here: the service runs
as a Windows **virtual account**, not LocalSystem, with access to exactly one thing beyond
default service privileges (the data directory). Do not grant it network shares, additional
file system paths, or Active Directory permissions. If you find yourself needing to, the
right fix is almost always a feature request for the product's own configuration surface,
not a privilege grant to this account.

## Certificate

Replace the self-signed HTTPS certificate the service generates on first start with one
issued by your organisation's CA (Admin Guide → Certificate replacement) before this server
is used for anything beyond a quick evaluation. The self-signed certificate protects the
same TLS channel cryptographically, but gives a user's browser no basis to verify the
server's identity, which matters more the more the deployment is used.

## Accounts, roles, and MFA

- Four fixed roles (Administrator, Operator, Auditor, ReadOnly) — grant the least role that
  lets someone do their job. An Auditor can run reports and read the audit log without
  being able to change configuration; use it for compliance/security staff who need
  visibility, not control.
- Passwords are Argon2id-hashed at rest; a configurable minimum length is enforced at
  account setup and every later password change.
- **TOTP multi-factor authentication** is self-service (Account → Security) using any
  standard authenticator app, and is **enforced at sign-in**: once enabled on an account,
  signing in requires a correct current code (or an unused recovery code) after the
  password, every time — a correct password alone is not enough. A wrong code is rate
  limited independently of the password lockout; too many wrong codes in a row discards
  that sign-in attempt and the admin must enter their password again.
- Review the audit log (Settings → Audit log) periodically — every login, configuration
  change, and export is recorded there, in order, for the life of the install.

## API keys

The Windows Event Log intake authenticates an external forwarder via an API key
(`X-Api-Key` header), stored SHA-256-hashed at rest and shown to you exactly once at
creation — copy it somewhere safe immediately; it cannot be retrieved again, only revoked
and replaced (Settings → Listeners). Treat this key like a password: it grants a forwarder
the ability to write Windows Event Log data into this server.

## Config bundles

Exported configuration bundles (streams, rules, devices, users, dashboards, reports, vendor
parser packs) are ECDSA-signed. Importing one from a signer you have not already trusted
prompts you to explicitly trust that signer's key first — a bundle from an untrusted signer
cannot silently apply. Only trust a signer you actually recognise; a bundle is a
configuration-changing payload, and trusting the wrong signer is equivalent to letting that
party configure your server.

## Self-monitoring

The collector emits its own health as events into a reserved `collector.health` stream —
disk pressure, queue depth, listener state, and archive verification results. Point a rule
or alert at that stream (or just glance at the built-in self-monitoring view, Settings →
Self-monitoring) rather than assuming silence means health; a device that stops sending
logs and a collector that has quietly stopped listening both look identical from the
outside unless something is actively watching for it.

## Patching

Keep the Windows Server host and the ASP.NET Core Runtime prerequisite current — this
product's own security posture assumes a patched host underneath it. Watch the Release
Notes for this product's own security-relevant updates and apply them promptly; an upgrade
preserves your database and configuration (Admin Guide → Upgrading).

## What this product deliberately does not do

- It does not encrypt the SQLite database file at rest by default — file-system-level
  protection is the data directory's restrictive ACLs (service account + Administrators
  only), not application-level encryption. If your compliance requirements mandate
  encryption at rest independent of file permissions, apply Windows BitLocker (or
  equivalent full-disk encryption) to the volume hosting the data directory.
- It is not internet-facing and has no design accommodation for being made so safely — see
  Network placement above.
