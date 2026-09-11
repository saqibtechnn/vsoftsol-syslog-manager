# Phase 11 — security evidence

Per SECURITY_STANDARDS.md §8. See `docs/security/THREAT_MODEL.md` (B1 and B5 redrawn — the
scheduled Phase 11 review point), `docs/security/ASVS-checklist.md` (Phase 11 L2 pass + the
completion sweep closing every remaining control), and `docs/security/SECURITY_REVIEW.md`
(the full structured penetration checklist + the Phase 11 gate). This file indexes the
tests that back each claim.

## TLS mutual authentication (STRIDE: S)

- `TlsSyslogListenerTests.StartAsync_AFramedMessageOverTls_IsAcceptedIntoTheChannel` —
  server-authenticated TLS works end to end.
- `TlsSyslogListenerTests.StartAsync_MutualTls_AnUntrustedClientCertificate_IsRejected` —
  a client certificate not on the trusted-thumbprint allow-list never reaches the ingest
  channel.
- `TlsSyslogListenerTests.StartAsync_MutualTls_ATrustedClientCertificate_Succeeds` — the
  positive case, proving the negative case isn't just "nothing works."

## SNMP community validation and BER fuzz (STRIDE: S, T)

- `SnmpTrapListenerTests.StartAsync_ATrapWithTheWrongCommunity_IsSilentlyDropped_NotIngested`,
  `StartAsync_NoNonDefaultCommunityConfigured_RefusesEveryTrap` — the default-`public`
  refusal and wrong-community rejection, over a real UDP socket.
- `SnmpBerReaderTests` (unit) — truncated datagram, unsupported version, oversized OID,
  malformed varbind, too-many-varbinds, protocol-confused PDU tag: 6/6 rejected without
  throwing.

## Windows Event Log authentication, source-scoping, rate limiting (STRIDE: S, D)

- `WinEventLogListenerTests.PostWithoutAnApiKey_IsRejectedWithUnauthorized_NotIngested`,
  `PostWithAWrongApiKey_IsRejected` — no forgery without the key.
- `PostFromAnUnexpectedSource_WhenTheKeyIsSourceScoped_IsRejected_NotForged` — a
  source-scoped key does not authenticate a request from anywhere else, the literal
  "unable to be used to forge events attributed to another host" requirement.
- `PostMoreThanTheConfiguredRateLimit_IsThrottledWithTooManyRequests` — the independent
  per-source rate limit.

## Config bundle attack matrix (STRIDE: T, D, E)

XXE and zip-slip are not separately fuzzed: the format is pure JSON with no XML or zip
container anywhere, so neither mechanism exists to attack — the same "provably absent by
construction" defence as the Phase 10 archive format.

- `ConfigBundleTests.RoundTrip_ExportFromA_ImportToCleanB_RulesStreamsDevicesReappearIdentically`
  — the full export/import round trip, including a vendor extractor file, across two
  independent SQLite databases.
- `ApplyAsync_AnUntrustedSigner_IsRefused_BeforeAnyContentIsProcessed` — trust-on-first-use
  enforcement; zero rows written for an untrusted signer.
- `ApplyAsync_ATamperedDocument_FailsSignatureVerification` — a single-byte content change
  after signing is caught.
- `TryVerify_AnOversizedDocument_IsRefused`, `TryVerify_AMalformedSignature_IsRefused` —
  schema/size validated before any cryptographic work, malformed input never throws.
- `ImportExtractors_APathTraversalFileName_NeverEscapesThePatternsRoot` — a `"../../"`
  vendor name and file name in the `extractors` section never write outside the configured
  patterns directory.

## TOTP MFA (STRIDE: S)

- `TotpGeneratorTests.GenerateCode_RfcTestVector_MatchesTheKnownAnswer` — RFC 6238 Appendix
  B known-answer vector, not just "some code was generated."
- `RecoveryCodeGeneratorTests` + `SqliteMfaRecoveryCodeStoreTests.TryConsumeAsync_TheCorrectUnusedCode_SucceedsExactlyOnce`
  — single-use enforcement at the database layer (an atomic `UPDATE ... WHERE used_utc IS
  NULL`), reuse rejected.
- Recorded gap: login-flow enforcement is not yet wired — B11-3, `known-issues.md`,
  `SECURITY_REVIEW.md`.

## A genuine bug found live during this phase, not by review

`Rfc3164Parser`'s BSD-tag detection wrongly stripped a CEF-formatted message's `"CEF:"`
prefix (a colon anywhere in the first token was accepted as a tag terminator, with no
requirement that a real space follow it). Caught by the pre-existing Phase 3
`OracleDifferentialTests` the moment the checkpoint-gaia vendor pack's fixtures exercised
it — exactly the class of defect differential/oracle testing exists to catch. Root-caused
and fixed at the source; verified safe against the full 1041-test unit suite. Full account
in `known-issues.md` and memory (`rfc3164-tag-requires-colon-space`).
