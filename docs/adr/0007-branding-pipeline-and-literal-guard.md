# ADR 0007 — Branding as a build-time pipeline; scope of the literal guard

**Status:** Accepted (Phase 0)

## Context

BRANDING.md requires that product name, vendor name, URLs, colours, and every image come
from `branding/` and `brand.json`, so a reseller rebrands with a file drop and a rebuild.
CLAUDE.md Constraint 11: `A grep for "VSoftSol" under src/ must return only the generated
BrandingInfo.g.cs`.

## Decision

- A console tool, **`build/VSoftSol.Syslog.BrandingGen`** (depends only on ImageSharp),
  runs before `VSoftSol.Syslog.Core` compiles. It:
  - generates `src/VSoftSol.Syslog.Core/Generated/BrandingInfo.g.cs` (git-ignored) with
    the brand strings and colours as `const`s;
  - derives every operator-omitted asset (wide/mono logo, favicon, app icon, installer
    BMPs) into `wwwroot/branding/` and `installer/assets/` (both git-ignored);
  - writes `--brand-primary` / `--brand-accent` to `wwwroot/branding/brand.css`;
  - treats a missing `branding/logo.png` as a **warning + placeholder**, never a failure;
  - content-compares every write, so two builds are byte-identical.
- It is invoked from `build/branding.targets` via `<Exec>` using `$(DOTNET_HOST_PATH)`.
  Core takes a `ReferenceOutputAssembly="false"` project reference on the tool purely for
  build ordering — this creates **no IL dependency**, which the architecture fitness test
  verifies.
- Design-time / analysis-only builds (IDEs, Buildalyzer, Stryker) skip the pipeline; an
  `EnsureBrandingInfoStub` target then emits a clearly-fake `BrandingInfo.g.cs` so Core
  still compiles. Core has no compile-time dependency on the brand values.

## Scope of the literal guard (interpretation)

The mandated solution layout in CLAUDE.md fixes the assembly/namespace token
`VSoftSol.Syslog.*`, so a literal grep for `VSoftSol` under `src/` necessarily matches
namespaces and `.csproj` files. The guard (`BrandingLiteralTests`) therefore targets the
brand **values** — the product name string, vendor name, vendor URL, support address,
copyright line, and the two hex colours — in `src/` source and config, allowing only
`Generated/BrandingInfo.g.cs` and the generated `wwwroot/branding/` output. The intent of
Constraint 11 (no hardcoded branding, rebrand without code changes) is fully met and is
proven by `docs/evidence/phase-00/rebrand-acceptance.txt`.

## Cost accepted

- One `<Exec>` in the build and a small console project not shipped with the product.
- `ImageSharp` (Apache-2.0, 2.1.x line) as a build-only dependency, SCA-scanned.
