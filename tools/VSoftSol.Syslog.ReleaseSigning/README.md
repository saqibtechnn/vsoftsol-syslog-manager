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
  "C:\build\out\VSoftSolSyslogManagerUpdate-1.1.0.msi" \
  1.1.0 \
  "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/download/v1.1.0/VSoftSolSyslogManagerUpdate-1.1.0.msi" \
  "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/tag/v1.1.0" \
  "<private-key-base64>"
```

The private key may be given as the last argument or via the `VSOFTSOL_RELEASE_PRIVATE_KEY`
environment variable — never both, never neither. Prefer the environment variable on a
shared or logged shell (a CLI argument can end up in shell history).

## The manual release-cut checklist

There is no CI-assisted release automation — the private key must never touch CI, so every
release is cut by hand, offline, by whoever holds the key:

1. **Bump the version in one place**: `Directory.Build.props`'s `VersionPrefix`. The MSI
   (`installer/Installer.wixproj`) and the new-install bundle (`installer/Bundle/Bundle.wixproj`)
   both read it, so the artifacts carry the version you are about to sign. Do this first,
   before building.

2. **Build and publish** the product, producing the two release files (see
   `installer/README.md` for the full order): `dotnet build -c Release`, `dotnet publish`,
   then `dotnet build installer/Installer.wixproj -c Release` (the MSI) and
   `dotnet build installer/Bundle/Bundle.wixproj -c Release` (the bootstrapper). Release them
   as **`VSoftSolSyslogManagerUpdate-<version>.msi`** (the MSI) and
   **`VSoftSolSyslogManagerInstall-<version>.exe`** (the bootstrapper).

3. **Run `sign-release`** against the built MSI (see above), using the offline private key.
   This produces `update-manifest.json` alongside the MSI.

4. **Tag the release** in git (`vX.Y.Z`, matching what you signed).

5. **Create the GitHub release** under that tag, and **upload the files as release
   assets**: the Update MSI, the Install `.exe`, and `update-manifest.json` — the asset filenames must match
   `GitHubUpdateOptions.ManifestAssetName` (`update-manifest.json`) and
   `GitHubUpdateOptions.MsiAssetNameSuffix` (any name ending in `.msi`) for the deployed
   product's self-update checker to find them.

6. **Verify the release**, ideally on a real test install with self-update enabled: confirm
   "Check now" detects the new version, downloads it, and shows "ready to install" — and
   that a deliberately tampered or wrongly-signed manifest is correctly rejected and
   audited (see the plan's own Verification section).
