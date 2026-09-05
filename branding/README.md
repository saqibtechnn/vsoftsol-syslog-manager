# branding/

Drop your brand assets here. Only `logo.png` is required — every other asset is
generated from it at build time.

    branding/
      logo.png              <-- REQUIRED. PNG, transparent, >= 512x512
      logo-wide.png         optional
      logo-mono.png         optional
      favicon.ico           optional
      app-icon.ico          optional
      installer-banner.bmp  optional (493x58)
      installer-dialog.bmp  optional (493x312)
      brand.json            optional (product/vendor strings and colours)
      placeholder/          fallback assets, committed, do not delete

See `BRANDING.md` for the full specification and the list of every surface that
consumes these files.

To rebrand: replace `logo.png`, edit `brand.json`, rebuild. No code changes.
