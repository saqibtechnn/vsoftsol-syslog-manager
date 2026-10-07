# WiX installer projects

Two release artifacts are built from this folder:

| Artifact | Project | Purpose |
|---|---|---|
| `VSoftSolSyslogManagerSetup.msi` → released as **`VSoftSolSyslogManagerUpdate-<version>.msi`** | `Installer.wixproj` | The product itself. Upgrades an existing install in place (`MajorUpgrade`), keeping the database, certificate and settings. ~12 MB. This is the file the opt-in self-update checker looks for (any release asset ending `.msi`). |
| **`VSoftSolSyslogManagerInstall-<version>.exe`** | `Bundle\Bundle.wixproj` | New-install bootstrapper. Installs the ASP.NET Core 8 Hosting Bundle first **only if it is missing**, then the MSI above. The Hosting Bundle is embedded, so it works offline. ~118 MB. |

## Build order

```bash
dotnet publish src/VSoftSol.Syslog.Web -c Release -r win-x64 --self-contained false -o publish/Web
dotnet build installer/Installer.wixproj -c Release          # the MSI
dotnet build installer/Bundle/Bundle.wixproj -c Release      # the bootstrapper (needs the MSI first)
```

Both read one version, `VersionPrefix` in the repo-root `Directory.Build.props`.

## The embedded Hosting Bundle

`Bundle\prereq\dotnet-hosting-<version>-win.exe` (~107 MB, Microsoft-signed) is **never
committed**. `Bundle.wixproj`'s `EnsureHostingBundle` target downloads it from Microsoft on the
first build and fails the build unless its SHA-512 equals `HostingBundleSha512`. To move to a
newer Hosting Bundle, change `HostingBundleVersion` and `HostingBundleSha512` together (the hash
is on Microsoft's .NET download page).

## Testing a bundle without installing

`VSoftSolSyslogManagerInstall.exe /layout <folder> /quiet /log <file>` parses the bundle and
writes a log of what it detected (is the Hosting Bundle present? is the product installed?)
without installing anything. A real first-install run on a clean VM is still the only test of
the "prerequisite missing" path.
