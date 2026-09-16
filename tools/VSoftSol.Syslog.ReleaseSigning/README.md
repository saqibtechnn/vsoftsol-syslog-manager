# VSoftSol.Syslog.ReleaseSigning

Vendor-only CLI for the self-update trust anchor (v1.1 — [ADR 0021](../../docs/adr/0021-self-update-check-and-verified-download.md)).
Never shipped: no product project references this one, so it cannot end up in the
installer's published output regardless of glob patterns. It references only
`VSoftSol.Syslog.Core` (for `BundleSigner` and the `UpdateManifest` shapes) — no runtime
dependency on the rest of the product.

## Subcommands

### `generate-keys <repo-root>`

Run **once**, offline, to create the keypair the shipped product trusts:

```bash
dotnet run --project tools/VSoftSol.Syslog.ReleaseSigning -- generate-keys "E:\Claoud Projects\vSoftsol Syslog Manager"
```

Writes the **public** key to `release-signing/public-key.txt` (commit it) and prints the
**private** key to the console only — it is never written to disk by this tool. Copy it
into your own offline secure storage immediately and clear your terminal scrollback
afterward. See `release-signing/README.md` for the full custody/rotation story.

### `sign-release <msi-path> <version> <msi-url> <release-notes-url> [private-key]`

Run **once per published release**. Hashes the MSI, builds an `UpdateManifest`, signs it
with the private key, and writes `update-manifest.json` next to the MSI:

```bash
dotnet run --project tools/VSoftSol.Syslog.ReleaseSigning -- sign-release \
  "C:\build\out\VSoftSolSyslogManagerSetup-1.1.0.msi" \
  1.1.0 \
  "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/download/v1.1.0/VSoftSolSyslogManagerSetup-1.1.0.msi" \
  "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/tag/v1.1.0" \
  "<private-key-base64>"
```

The private key may be given as the last argument or via the `VSOFTSOL_RELEASE_PRIVATE_KEY`
environment variable — never both, never neither. Prefer the environment variable on a
shared or logged shell (a CLI argument can end up in shell history).

## The manual release-cut checklist

There is no CI-assisted release automation — the private key must never touch CI, so every
release is cut by hand, offline, by whoever holds the key:

1. **Bump the version in *two* places** — these are independent, hardcoded literals today,
   not wired to each other:
   - `Directory.Build.props`'s `VersionPrefix`
   - `installer/Installer.wixproj`'s `ProductVersion`

   Forgetting either one is a real gap this feature depends on but does not itself fix. Do
   this first, before building, so the built artifacts actually carry the version you're
   about to sign.

2. **Build and publish** the product per the normal release process (`dotnet build -c
   Release`, then `dotnet build installer/Installer.wixproj -c Release` to produce the MSI).

3. **Run `sign-release`** against the built MSI (see above), using the offline private key.
   This produces `update-manifest.json` alongside the MSI.

4. **Tag the release** in git (`vX.Y.Z`, matching what you signed).

5. **Create the GitHub release** under that tag, and **upload both files as release
   assets**: the MSI itself, and `update-manifest.json` — the asset filenames must match
   `GitHubUpdateOptions.ManifestAssetName` (`update-manifest.json`) and
   `GitHubUpdateOptions.MsiAssetNameSuffix` (any name ending in `.msi`) for the deployed
   product's self-update checker to find them.

6. **Verify the release**, ideally on a real test install with self-update enabled: confirm
   "Check now" detects the new version, downloads it, and shows "ready to install" — and
   that a deliberately tampered or wrongly-signed manifest is correctly rejected and
   audited (see the plan's own Verification section).
