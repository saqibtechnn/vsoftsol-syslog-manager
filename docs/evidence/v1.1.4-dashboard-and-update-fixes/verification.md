# Dashboard reproduction and self-update rehearsal — verification

Date: 2026-10-27. Environment: this dev host (Windows 11), an isolated copy of the app (own data folder, own
ports; the installed service on the machine was not touched), a local fake of the GitHub releases API, and a
**throwaway** signing key generated for the test (never the vendor key; the private half lives only in the
scratch folder and is not committed).

## A. Dashboard

Reproduced in a browser with the repo's vendor fixtures replayed over UDP (870 + 580 + 290 messages). Fixed
defects are in `red-green.md` §1–5. Before: widgets collapsed to ~59 px strips, severity widget fell below
its neighbour, "Recent criticals" listed informational events, long tables (audit) rendered no rows. After:
Network Overview renders as designed (Message rate + Severity on row 1, Top talkers + Recent criticals on
row 2; Recent criticals shows only Alert/Critical).

Observed and NOT changed (product decisions, listed for the owner):

1. **A new device is quarantined until approved, and the dashboards say nothing about it.** Messages from an
   unknown source create a "pending device" (a small red "1" in the navigation). Until it is approved,
   Device Health reads "No devices are visible to you yet" and the Getting-started box keeps "A device has
   sent its first message" unticked — although the Network Overview is already full of that device's
   messages. A new admin reads this as "nothing works". Suggested: a prominent "N devices waiting for
   approval — review" banner on every dashboard, tick the checklist step when a pending device exists, and
   offer "auto-approve devices on the first run".
2. **Hostname-less syslog shows as "(none)"** in Top talkers and a dash in the Host column. RFC 3164 messages
   often carry no hostname; the widgets should fall back to the source IP / resolved device name.
3. The default dashboards are thin (4 widgets each). Content, not a defect — see the product roadmap.

## B. Self-update

### What the product does today (by design, ADR 0006 / 0021)

Checks GitHub (opt-in, off by default), verifies a signed manifest against ONE compiled-in public key,
downloads the installer, re-hashes it, and offers an authenticated "Download the installer" link. **It does
not install by itself**: the service account has no rights to replace `Program Files` or restart itself, and
ADR 0021 deliberately chose "verify automatically, Administrator installs" over adding an elevated helper.

### Rehearsal results (real build with a baked-in throwaway key, local fake GitHub)

| Scenario | What the fake release was | Result |
|---|---|---|
| Good release v1.1.4 | signed manifest + matching installer | **Update ready — version 1.1.4.** Installer staged; SHA-256 of published file = staged file = hash pinned in the signed manifest (`61982c17…110a`); download link served only to a signed-in admin (302 to login otherwise) |
| Tampered manifest | manifest edited after signing | Rejected; audit `update.signature_verification_failed`; **installer never downloaded** |
| Tampered installer | valid manifest, one byte of the installer changed | Downloaded, hash mismatch, discarded (staging folder empty); error recorded; `/updates/download` → 404 (nothing served) |
| Not newer (v1.1.3 = current) | valid, same version | No download; "Up to date" |
| No manifest asset | installer only (exactly how v1.1.1–v1.1.3 are published on GitHub today) | Nothing offered; error "release v1.1.4 has no ManifestAssetName asset" (internal jargon shown to the admin) |

A first run of this same rehearsal exposed bug §6 in `red-green.md` (version parser) — the very first check
reported "Up to date" for a correctly signed newer release. Fixed test-first; the table above is from the
re-run on the fixed build.

### Installing from a file (the "Update" MSI), checked without installing

The v1.1.2 and v1.1.3 Update installers were opened as databases (no install performed). Both carry the same
`UpgradeCode {6F2A1D3E-6C2B-4F2E-9A3F-2B7D6A8C9E01}`, different `ProductCode`s, `ProductVersion` 1.1.2 / 1.1.3,
`RemoveExistingProducts` scheduled (standard major upgrade) and an `Upgrade` row that detects older versions
and one that only-detects newer ones (downgrade blocked). So running the v1.1.3 MSI over any older install
upgrades it in place. **An actual upgrade run was not performed here** — that needs a clean machine; the
installed copy on this host was left alone.

### What is still missing for "updates over the internet" to work for customers

1. **No signing key exists in any shipped build.** `release-signing/public-key.txt` was never committed, so every
   build says `HasRealKey = false` and the checker fails closed ("no release-signing key is configured in this
   build"). The vendor must generate the keypair once, offline (`generate-keys`), keep the private key in
   custody, and commit the public key.
2. **Releases carry no `update-manifest.json`.** The checker looks for that asset next to the `.msi`;
   `sign-release` produces it, once per release, with the offline private key. v1.1.1–v1.1.3 have none.
3. **Already-installed 1.1.x copies can never verify an update** (they have no key baked in). Customers must
   install, by hand, the first release that carries the key; every later one can then be checked and
   downloaded in-app.
4. **Automatic installation is not implemented** (see "What the product does today"). Doing it means a new
   least-privilege updater service or scheduled task that installs only MSIs verified against the same key —
   a design change that needs an ADR and the owner's decision.
5. Minor: the Updates page keeps showing the earlier "Update ready" message after a later check fails
   (the download itself is correctly refused), and the "no manifest" message exposes an internal option name.
