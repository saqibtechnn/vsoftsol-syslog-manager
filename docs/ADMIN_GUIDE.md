# VSoftSol Syslog Manager — Admin Guide

For the person who installs, upgrades, and keeps this server running. If you are looking
for search syntax, rules, dashboards, or reports, see the [User Guide](USER_GUIDE.md).

## Install

1. Install the **ASP.NET Core Runtime 8.0.x (Hosting Bundle)** first, if it is not already
   present: <https://dotnet.microsoft.com/download/dotnet/8.0>. The installer checks for a
   .NET runtime and stops with a clear message if none is present, but it cannot verify the
   exact version — if the service fails to start after install, this is the first thing to
   check.
2. Run `VSoftSolSyslogManagerSetup.msi`. It installs to `C:\Program Files\VSoftSol Syslog
   Manager`, creates the data directory at `C:\ProgramData\Vision Software
   Solutions\VSoftSol Syslog Manager` with permissions restricted to the service account and
   Administrators, registers one Windows Service, and opens three Windows Firewall rules
   (UDP 514, TCP 514, TCP 5443).
3. Open `https://<this-server>:5443` in a browser. You will land on the first-run wizard —
   five short steps: administrator password, listener ports, a look at the data directory,
   a retention preset, and an optional vendor configuration bundle. It cannot be skipped or
   returned to once finished.
4. After the wizard, the "Waiting for messages" page shows copy-ready configuration commands
   for your device, with this server's address and port already filled in. The dashboard
   opens automatically the instant the first message arrives.

There is no second console and no configuration file to hand-edit for normal operation —
every setting the product exposes is reachable from the web UI (Settings).

## The Windows Service

One service is installed, plainly named **VSoftSol Syslog Manager** (see `services.msc`).
It hosts the syslog listeners, the ingest pipeline, the rules/alerts/retention engines, and
the HTTPS admin dashboard all in one process (see `docs/adr/0005-single-service-over-split-processes.md`).
It runs under a dedicated **virtual service account** (`NT SERVICE\VSoftSol Syslog
Manager`) rather than LocalSystem — this account exists only for the lifetime of the
service and cannot log in interactively; it has no password to manage or rotate.

The service is configured for **delayed automatic start** (it starts a short while after
boot, once the rest of the system is up) and restarts itself automatically if it exits
unexpectedly.

## Upgrading

Run the newer version's MSI directly over an existing install — no need to uninstall
first. `MajorUpgrade` handling removes the old file version and preserves:

- the database and every row in it (events, streams, rules, alerts, dashboards, reports,
  users, audit log)
- the data directory and its ACLs
- the HTTPS certificate (unless you have already replaced it — see below, which is also
  preserved)
- the bootstrap configuration override file (listener ports, if you changed them from the
  wizard's defaults)

The service is stopped and restarted automatically as part of the upgrade. Expect a brief
gap in ingestion (a few seconds to low tens of seconds depending on database size) while
the new version starts; the syslog listeners' OS-level socket buffers absorb a short gap,
but a very long one can still drop UDP datagrams sent during the window — schedule upgrades
outside your busiest ingest period if that risk matters to you.

## Backup and restore

The database (`syslog.db`, in the data directory) is the only place product state lives —
per the "one configuration surface" rule, there is no separate settings file to remember.
Back up the whole data directory; there is nothing else to capture.

**What to back up** — the entire data directory, by default:
```
C:\ProgramData\Vision Software Solutions\VSoftSol Syslog Manager\
```
which contains `syslog.db` (and its `-wal`/`-shm` files if the service is running),
`config\bootstrap-overrides.json` (listener ports, if changed from the wizard's defaults),
`certs\web-https.pfx` (the HTTPS certificate), `logs\`, and `spill\` (in-flight message
buffer — safe to exclude from a backup; it is transient, and a missing spill directory is
recreated automatically on next start).

**Backup procedure** (PowerShell, run as Administrator):
```powershell
Stop-Service "VSoftSol Syslog Manager"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dest = "D:\Backups\vsoftsol-$stamp"
robocopy "C:\ProgramData\Vision Software Solutions\VSoftSol Syslog Manager" $dest /E /XD spill /R:2 /W:5
Start-Service "VSoftSol Syslog Manager"
```
Stopping the service first guarantees a consistent copy — SQLite checkpoints and closes its
WAL file cleanly on a graceful shutdown, so the copied `syslog.db` is immediately usable
with no repair step. Copying a live database while the service keeps running is possible
with SQLite's own online-backup mechanism, but is not what this procedure uses; stopping
the service for the (typically well under a minute) duration of the copy is the simpler,
safer default for a small-to-mid-size install, and is what this guide tests and recommends.

**Restore procedure** — reverse of the above:
```powershell
Stop-Service "VSoftSol Syslog Manager"
Remove-Item "C:\ProgramData\Vision Software Solutions\VSoftSol Syslog Manager\syslog.db*" -Force
robocopy "D:\Backups\vsoftsol-20260101-020000" "C:\ProgramData\Vision Software Solutions\VSoftSol Syslog Manager" /E /R:2 /W:5
Start-Service "VSoftSol Syslog Manager"
```

**Tested**: this exact copy/restore sequence (minus the actual `Stop-Service`/`Start-Service`
calls, which need a live Windows Service to demonstrate against) was run against a
synthetic data directory during Phase 12 development — a directory containing a
representative `syslog.db`, `config\bootstrap-overrides.json`, and `certs\web-https.pfx`
was copied to a backup location, the original files were deleted, and the backup was copied
back; a byte-for-byte comparison of every file confirmed the round trip is lossless. See
`docs/evidence/phase-12/backup-restore.md` for the exact commands and output.

## Certificate replacement

The service generates a self-signed HTTPS certificate on first start if none exists at
`<data directory>\certs\web-https.pfx`, so the dashboard is reachable over HTTPS
immediately, with no setup step. Browsers will show a certificate warning for this
self-signed certificate — expected, and safe to accept for an internal tool, but you should
replace it with a certificate from your organisation's CA for anything beyond a quick
evaluation.

To replace it:
1. Obtain a certificate + private key for this server's hostname, exported as a `.pfx` file
   (a password is optional; the file's confidentiality is protected by the data directory's
   ACLs, not a PFX password).
2. Stop the service.
3. Replace `<data directory>\certs\web-https.pfx` with your file, keeping the exact
   filename.
4. Start the service.

The service never overwrites an existing certificate file — it only generates one when the
file is missing.

## Port configuration

The first-run wizard's second step sets the syslog UDP port, syslog TCP port, and the web
dashboard's HTTPS port (defaults 514, 514, and 5443). These are **bootstrap-tier**
settings — read once at service start, before the database is even opened — unlike every
other setting in the product, which lives in the database and takes effect immediately from
the UI.

If you need to change a port after setup (a conflict with something else on the server, for
example), the wizard cannot be re-run.

**Syslog UDP/TCP ports (v1.1+)**: change these from **Settings → Listeners** — an "Apply"
button changes the running listener immediately, no restart, with a confirmation before it
takes effect (the new port is bound before the old one closes, so a mistyped or
already-used port is refused with the previous port left untouched). This only applies live
in the packaged, installed service; a plain `dotnet run` dev session that isn't hosting the
collector runtime shows a restart-required message instead.

**Web dashboard HTTPS port**, and UDP/TCP as a fallback when the live option above is not
available: edit `<data directory>\config\bootstrap-overrides.json` directly —
```json
{
  "Ingestion": { "UdpPort": 1514, "TcpPort": 1514 },
  "Kestrel": { "Endpoints": { "Https": { "Url": "https://0.0.0.0:8443" } } }
}
```
and restart the service. This is the one file in the product an administrator may need to
hand-edit, and only for these bootstrap-tier settings — everything else stays UI-driven.

## Data directory

The first-run wizard's third step shows, but does not let you change, the data directory
the installer already provisioned with the correct ACLs. Relocating it afterward — to a
larger disk, for example — is a manual procedure: the service account is deliberately
least-privileged (see **Service account** below) and cannot grant itself access to an
arbitrary new path or restart its own service, so there is no live, UI-driven option for
this, unlike the port changes above.

1. **Stop the service.**
   ```powershell
   Stop-Service "VSoftSol Syslog Manager"
   ```
2. **Move the data directory** to the new location:
   ```powershell
   robocopy "C:\ProgramData\Vision Software Solutions\VSoftSol Syslog Manager" "D:\SyslogData" /E /MOVE /R:2 /W:5
   ```
3. **Re-apply the restrictive ACLs** on the new path — the service account and
   Administrators only, matching what the installer set on the original directory:
   ```powershell
   icacls "D:\SyslogData" /inheritance:r
   icacls "D:\SyslogData" /grant:r "NT SERVICE\VSoftSol Syslog Manager:(OI)(CI)F"
   icacls "D:\SyslogData" /grant:r "Administrators:(OI)(CI)F"
   ```
4. **Point the service at the new path.** The data directory is a bootstrap-tier setting
   read before the database (and therefore before `bootstrap-overrides.json` inside it) can
   be consulted, so it cannot be set there — it is read from the service's own process
   environment instead, using the standard Windows Service Control Manager per-service
   `Environment` registry value:
   ```powershell
   Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\VSoftSol Syslog Manager" `
     -Name Environment -Value @("Collector__DataDirectory=D:\SyslogData")
   ```
   (The installer also writes `HKLM\Software\Vision Software Solutions\VSoftSol Syslog
   Manager\DataDirectory` — that key is Windows Installer bookkeeping only, used for
   repair/uninstall, and is never read by the running service; changing it has no effect and
   you do not need to touch it.)
5. **Start the service** and confirm the dashboard loads and a test message lands correctly
   before removing the old directory:
   ```powershell
   Start-Service "VSoftSol Syslog Manager"
   ```

## Service account

The service runs as the virtual account `NT SERVICE\VSoftSol Syslog Manager`, not
LocalSystem — deliberately, per `SECURITY_STANDARDS.md`'s least-privilege requirement (see
`docs/adr/0006-least-privilege-service-account.md`). This account has no password, cannot
sign in interactively, and has been granted access to exactly one thing beyond default
Windows service privileges: full control of the data directory. It has no other file
system, network share, or Active Directory permissions. Do not grant it anything further —
if a task seems to need more access than this, it almost certainly belongs in the product's
own configuration surface instead, not a service-account privilege escalation.

## Troubleshooting

**Service won't start** — check the Windows Event Log (Application log, source "VSoftSol
Syslog Manager") for the startup error. The most common causes are: the ASP.NET Core
Runtime is missing or the wrong version; the configured ports are already in use by another
process; or the data directory's ACLs were altered by something outside the installer
(restore them via Explorer's Security tab, or repair the installation from Programs and
Features).

**Dashboard unreachable but service is running** — confirm the firewall rule for the
configured HTTPS port is present (`Get-NetFirewallRule -DisplayName "*VSoftSol*"`) and that
you are browsing to the right port if you changed it from the default 5443.

**No messages arriving** — confirm the device is actually configured to send to this
server's address and the port you configured (the "Waiting for messages" page and the User
Guide's Device Compatibility chapter both show the exact commands per vendor); check
Windows Firewall on this server for the UDP/TCP syslog port; and check the sending device's
own firewall/ACLs for outbound traffic to this server.

**A message arrived but doesn't look parsed** — every message is stored and searchable
even when no parser recognises it (Constraint 4 — nothing is ever rejected for coming from
an unrecognised vendor). Unparsed messages land in the built-in **Parse Failures** stream,
raw text intact. See the User Guide's Device Compatibility chapter for how to add a parser
pattern without a code change or a new release.
