# Release signing (v1.1 self-update)

This directory holds the **public** half of the ECDSA P-256 keypair the shipped product
trusts when verifying a self-update. It is baked into every build via
`build/releasesigning.targets` into `ReleaseSigningInfo.g.cs` (generated, not committed).

## Why this is not trust-on-first-use

Config bundles (ADR 0019) use trust-on-first-use — an Administrator accepts an unknown
signer's fingerprint the first time they see it. Self-update deliberately does **not** work
that way: the only acceptable signer is the one public key committed here, decided once by
the vendor, not by whichever key happens to arrive with the first update manifest a
compromised distribution channel could serve. See `docs/adr/0021-self-update-check-and-verified-download.md`.

## `public-key.txt`

One line: the base64 `SubjectPublicKeyInfo` of the release-signing keypair. **Safe to
commit** — it is a public key, there is no secrecy requirement on it.

If this file is missing or blank, every build emits a warning
(`VSOFTSOL-UPDATE-001`) and bakes in a placeholder key with
`ReleaseSigningInfo.HasRealKey = false` — self-update signature verification then always
fails closed ("not configured"), it never silently trusts the placeholder.

## The private key

**Never commit the private key. Never embed it in shipped code. Never let it touch CI.**

Generate the keypair once, offline, with the tool in
`tools/VSoftSol.Syslog.ReleaseSigning/`:

```bash
dotnet run --project tools/VSoftSol.Syslog.ReleaseSigning -- generate-keys "E:\Claoud Projects\vSoftsol Syslog Manager"
```

This prints the private key **to the console only** — it is never written to disk by the
tool — and writes the public key straight into this file. Copy the private key into your own
offline secure storage immediately (a password manager, an HSM, an encrypted offline
volume — whatever you already use for other long-lived secrets) and clear your terminal
scrollback afterward. If you lose it, generate a fresh keypair and every build after that
point trusts the new one — but a build published before the loss still only trusts the old
key, so lost private keys should be treated as a reason to publish a new build promptly
rather than something to defer.

Use the same tool's `sign-release` subcommand to sign each future GitHub release — see
`tools/VSoftSol.Syslog.ReleaseSigning/README.md` for the full release-cut checklist.

## Key rotation

There is no rotation mechanism in v1 — one embedded key, generated once. If it is ever
compromised, shipping a newly-trusted key requires a full product update through this same
mechanism (a build with the new public key baked in, published and verified against the
*old* key one last time). This is an accepted limitation, not an oversight — see
`docs/security/THREAT_MODEL.md`'s B6 section and the ADR.
