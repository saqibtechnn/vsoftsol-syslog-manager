# ADR 0019 — Phase 11 hardening: TLS/SNMP/Windows Event Log intake, config bundles, self-monitoring, MFA

## Status
Accepted — `v1.0.0-phase.11`.

## Context
Phase 11 adds the remaining intake protocols and the security/operability work required
before the product goes on a production network: a TLS syslog listener, an SNMP trap
receiver, a Windows Event Log intake endpoint, signed config bundles for portability and
vendor-pack distribution, collector self-monitoring, and TOTP MFA. `Protocol` and the
`listeners`/`events.protocol` CHECK constraints already carried `tls`/`snmp`/`wineventlog`
since Phase 0/1 — this phase is the first to actually implement them.

## Decision 1 — new listeners reuse the existing frame/parse pipeline, not a parallel one

`TlsSyslogListener` is structurally a `TcpSyslogListener` whose per-connection stream is an
authenticated `SslStream` instead of the raw socket — same accept loop, same connection
cap, same `SyslogStreamFramer`, deliberately not shared by inheritance (a TLS-only change
must never put the working Phase 2 TCP path at risk).

SNMP traps and Windows Event Log entries are not RFC 3164/5424 syslog at all, so
`MessageParser.Parse` branches on `frame.Protocol` before the RFC chain and delegates to
`SnmpTrapNormalizer`/`WinEventLogNormalizer`, which produce the same `SyslogParseResult` +
`EventField` shape every other source does. Both report `ParseStatus.Raw` — not because
they are unparsed, but because the `events.parse_status` CHECK constraint
(`'raw'|'rfc3164'|'rfc5424'`) was deliberately narrow from Phase 0 and widening it would
require the full SQLite table-rebuild procedure (`events` has no `ALTER ... DROP
CONSTRAINT`) for a cosmetic distinction the `protocol` column already carries. This
required one real fix: `MessageParser.BuildEvent`'s "`Raw` ⇒ blank the `message` column"
convention is correct for genuinely-unparsed syslog text but wrong for these two sources,
which always carry a real decoded message — the condition now excludes them explicitly.

Every listener still lands in the same `FrameIntake` → channel → spill → pipeline path, so
the durability guarantees (Constraint 3) are identical regardless of protocol.

## Decision 2 — SNMP: a minimal bounded BER reader, no MIB, no external library

`SnmpBerReader` decodes exactly the ASN.1 tags an SNMPv1/v2c trap uses. Every length is
bounds-checked against the buffer before use, OID arc count and varbind count are capped,
and indefinite-length BER is rejected outright — a malformed or hostile datagram returns
`false`, never throws, never allocates unbounded ("MIB-free varbind capture" per
VENDOR_SUPPORT.md Tier 3; a MIB compiler is explicitly out of scope). The community string
is validated by `SnmpTrapListener` *before* the frame ever reaches `FrameIntake` — a wrong
community, or no non-default community configured at all, drops the datagram and increments
a per-listener counter, exactly mirroring how a real SNMP agent silently discards it. The
listener refuses to accept traps until the community secret is set to something other than
`public` (SECURITY_STANDARDS.md).

## Decision 3 — Windows Event Log: an authenticated HTTP endpoint inside the collector host, not a new web app

Windows has no native syslog client. `WinEventLogListener` implements `ISyslogListener`
over the in-box `HttpListener` — no ASP.NET Core dependency for one POST endpoint — so a
forwarder authenticates with an API key (`X-Api-Key`, SHA-256-hashed at rest, shown once at
creation) and, optionally, a single allowed source IP the key is scoped to (closing "unable
to be used to forge events attributed to another host"). It binds loopback by default and
lives inside the *collector* process (not the Web UI process) so it durably feeds the same
channel/spill/pipeline as every other listener. A minimal fixed-window per-source rate
limit (not the ingest path's token-bucket sophistication — this endpoint is authenticated
and inherently low-volume) satisfies "rate-limited."

**Known deployment note, not fixed in this phase**: `HttpListener` binding to a wildcard
prefix requires a Windows URL ACL reservation (`netsh http add urlacl`) for a non-admin
service account; loopback-specific prefixes (what ships by default) do not. Carried to the
Phase 12 installer as a one-line ACL step if an operator widens the bind address.

## Decision 4 — config bundles: pure JSON, ECDSA-signed, trust-on-first-use, generic export / allow-listed import

**Format.** A bundle is one JSON document (header + one JSON array per section) plus a
detached ECDSA P-256 signature, public key, and fingerprint. There is no zip container and
no XML anywhere in the format — XXE and zip-slip cannot occur, not because input is
sanitised, but because neither mechanism exists to exploit, the identical defence used for
the Phase 10 archive format.

**Signing and trust.** Every installation generates its own ECDSA key pair on first use
(private half in the DPAPI-protected `secrets` table, public half + fingerprint in
`bundle_signing_identity`) and signs everything it exports with it. Import verifies the
signature against the bundle's *own* embedded public key, then checks whether that key's
fingerprint is in `bundle_trusted_signers` — if not, the Administrator is shown the
fingerprint and must explicitly accept it (recorded with who and when) before any content
is processed. This is deliberately trust-on-first-use, not a PKI: a full CA hierarchy is
unwarranted for point-to-point transfer between an operator's own installs or a vendor
pack, and TOFU is the same model SSH already normalised for this exact problem.

**Export is generic; import is allow-listed.** `ConfigBundleExporter` reads each section
with `SELECT *` and serialises every column by name straight from the reader's schema — new
columns a future phase adds are exported automatically, with `users.password_hash` as the
one hand-maintained exclusion. `ConfigBundleImporter` is deliberately **not** symmetric:
every column name it writes comes from a fixed constant in the importer's own code, never
from the bundle's JSON keys, so a hostile bundle cannot turn a crafted field name into a SQL
identifier. Rows are upserted by natural key (stream/rule/device/device-group name, report
`template_key`, dashboard `system_key`); personal (non-`is_system`) dashboards and reports
do not import — their ownership cannot be remapped across installs without a user-identity
bridge this format does not have (`known-issues.md` B11-1).

## Decision 5 — self-monitoring emits real events into a reserved stream; no parallel alert engine

The collector's own health (disk free, drop counter, sustained queue depth, listener down,
archive verification failure) is evaluated by the pure `SelfMonitoringEvaluator` (Core, no
I/O, edge-triggered — fires once on breach, once on clear, never every tick) against live
readings `SelfMonitoringService` (a collector-host `BackgroundService`) gathers each minute.
On a transition it does two things: appends a real `SyslogEvent` into a reserved
`collector.health` stream (seeded with `match_json = NULL`, `is_catch_all = 0`, which
`ConditionCompiler.Compile(null)` resolves to `CompiledCondition.MatchNothing` — inert for
live traffic; the service assigns the stream id directly via `SyslogEvent.StreamIds`,
bypassing the router) — and raises the notification directly through the existing
`NotificationSink`. The first half satisfies "the collector is monitored the same way
everything else is" (an operator can build their own dashboard, search, or alert against
this stream with zero new code); the second is what actually makes it a **self-alert**
without requiring an operator to have pre-authored a matching alert definition. No parallel
alerting mechanism was built.

"Listener down" is tracked by a small `ListenerHealthRegistry` (Ingestion) that
`IngestionHostedService` updates as each listener starts/stops, checked by protocol-name
prefix. This correctly detects a listener that never started or was gracefully stopped;
detecting an in-process listener silently crashing without going through `StopAsync` would
need supervision/restart logic this phase did not build (`known-issues.md` B11-2 — no
listener in the current codebase has an observed crash path that exits without it).

The Web host cannot read the collector process's in-memory counters directly (separate
process) — the self-monitoring page reads `collector_stat_samples` through the existing
Phase 9 `SystemSeriesReader`, the same table and reader the Collector Health dashboard
widget already uses, rather than inventing a second cross-process channel.

## Decision 6 — TOTP MFA: RFC 6238 as specified, self-service enrollment, login enforcement deferred

`TotpGenerator` uses HMAC-SHA1 because RFC 6238 mandates it for interoperability with every
standard authenticator app — not a weak-hash finding; TOTP's security is the shared
secret's entropy and HMAC's PRF property, which SHA-1 collision attacks do not threaten
(recorded as accepted-by-design in `SECURITY_REVIEW.md`). Recovery codes are high-entropy
random values, so a fast SHA-256 hash is correct (not Argon2id, which defends a low-entropy
*human-chosen* password against offline guessing — a different threat that does not apply
here). Enrollment is two-phase: a secret is generated and stored but not enforced until a
submitted code validates, at which point ten recovery codes are issued and shown exactly
once.

**Scope carried to a follow-up, not fixed in this phase**: the enrollment/verification/
recovery-code primitives are complete, tested, and available from a self-service account
page, but the login flow itself does not yet prompt for a second factor when
`users.mfa_enabled = 1` (`known-issues.md` B11-3). Every primitive the login flow will call
(`MfaSelfServiceService.VerifyLoginCodeAsync`) already exists and is tested; wiring it into
`LocalAuthenticationProvider`/`AuthEndpoints` is the remaining step.

## Consequences
- Nine new vendor packs (VENDOR_SUPPORT.md's extended seven, two of which — pfSense/OPNsense
  and the two Aruba AOS variants — ship as separate packs each) bring the loaded total to 17.
  Building them surfaced a genuine, general `Rfc3164Parser` bug (a colon-terminated tag
  requires the colon to be the token's last character — `"CEF:0|Vendor|..."` was being
  wrongly tag-stripped) caught by the Phase 3 oracle differential test and fixed at the
  root, verified safe against the full 1041-test unit suite.
- `IngestionServiceCollectionExtensions.AddSyslogIngestion` stays self-contained (Udp/Tcp
  only); the three delegate-dependent listeners are registered by `AddCollectorRuntime`
  alongside the delegates they need, so a narrower test harness building just
  `AddSyslogIngestion()` never fails to resolve a certificate/community/API-key provider it
  was never going to construct.
- Carried, documented items: B11-1 (personal dashboard/report bundle import), B11-2
  (listener-crash supervision), B11-3 (MFA login-flow enforcement), and the Phase 12
  URL-ACL installer step for a non-loopback Windows Event Log bind address.
