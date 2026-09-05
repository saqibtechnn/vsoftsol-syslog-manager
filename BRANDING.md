# BRANDING.md — Asset Pipeline

All product branding is **loaded from the `branding/` folder at build and install time**.
Nothing is hardcoded in source. The operator drops files in; the build consumes them.

---

## What the operator provides

Place these in `branding/`. Only `logo.png` is required — everything else is derived
from it if absent.

| File | Required | Spec | Used for |
|---|---|---|---|
| `logo.png` | **Yes** | PNG, transparent background, ≥ 512×512, square or near-square | Master source for every derived asset |
| `logo-wide.png` | No | PNG, transparent, ~800×200 | UI header, login page, PDF report header |
| `logo-mono.png` | No | PNG, single colour, transparent | Dark mode, watermarks, low-contrast surfaces |
| `favicon.ico` | No | multi-size .ico (16/32/48) | Browser tab |
| `app-icon.ico` | No | multi-size .ico (16/32/48/256) | MSI installer, Add/Remove Programs, service |
| `installer-banner.bmp` | No | BMP, 493×58 | WiX top banner |
| `installer-dialog.bmp` | No | BMP, 493×312 | WiX welcome/exit dialog |
| `brand.json` | No | see below | Colours and product strings |

### `brand.json`

```json
{
  "productName": "VSoftSol Syslog Manager",
  "vendorName": "Vision Software Solutions",
  "vendorUrl": "https://vsoftsol.com",
  "supportEmail": "support@vsoftsol.com",
  "primaryColor": "#0F4C81",
  "accentColor": "#2E9E6B",
  "copyright": "© 2026 Vision Software Solutions"
}
```

If `brand.json` is absent, use these values as defaults.

---

## What the build does

1. A pre-build MSBuild target (`branding.targets`) runs before compilation and:
   - Verifies `branding/logo.png` exists. If it does not, **log a build warning** naming
     the missing file and fall back to the placeholder in `branding/placeholder/`.
     The build must never fail because a logo is missing.
   - Derives any absent asset from `logo.png` using ImageSharp: favicon sizes, app icon
     sizes, wide logo (letterboxed on transparent), mono logo (flattened to
     `primaryColor`), and both installer BMPs.
   - Emits everything to `src/VSoftSol.Syslog.Web/wwwroot/branding/` and
     `installer/assets/`.
   - Generates `BrandingInfo.g.cs` in `Core` with the strings and colours from
     `brand.json` as compile-time constants.
2. Derived assets are **git-ignored**. Only the operator's source files in `branding/`
   are committed. Regenerating must be deterministic and idempotent.
3. `brand.json` colours are also written to CSS custom properties
   (`--brand-primary`, `--brand-accent`) consumed by the design system — so a colour
   change is one file edit, not a search across components.

---

## Where branding appears

| Surface | Asset | Phase |
|---|---|---|
| Login page | `logo-wide.png`, product name, vendor name | 4 |
| UI header / navigation | `logo-wide.png` (collapses to `logo.png` on narrow) | 4 |
| Browser tab | `favicon.ico`, title `<Page> — <productName>` | 4 |
| About page | `logo.png`, version, build date, vendor URL, copyright | 4 |
| Email notifications | `logo-wide.png` inline, vendor footer | 7 |
| PDF reports | `logo-wide.png` in header, copyright in footer | 10 |
| CSV/JSON exports | product name and version in the metadata header | 10 |
| Config bundle export | vendor and product name in the bundle manifest | 11 |
| MSI installer | `app-icon.ico`, both BMPs, vendor name, product name | 12 |
| Windows Service entry | `app-icon.ico`, display name = `productName` | 12 |
| First-run wizard | `logo-wide.png` on every step | 12 |
| Add/Remove Programs | `app-icon.ico`, publisher = `vendorName`, support URL | 12 |

---

## Rules

- **No image file is ever committed outside `branding/` and `branding/placeholder/`.**
- **No product name, vendor name, colour, or URL is written as a literal in any source
  file.** Read from `BrandingInfo` or the CSS custom properties. A grep for
  `"VSoftSol"` in `src/` should return only `BrandingInfo.g.cs`.
- Replacing the logo must require **only** dropping a new `logo.png` in `branding/` and
  rebuilding — no code change, no asset hunt.
- This makes the product white-labelable for a reseller or a customer with zero source
  changes, which is worth building correctly the first time.
