# Dashboard rendering and self-update fixes — red/green evidence

Per TESTING_STANDARDS.md §2.1: each test below was observed failing for the stated reason before the
fix. Reproduction was done against a real isolated instance (own data folder, own ports, replaying the
repo's vendor fixtures over UDP) driven in a browser — not only through server-rendered HTML, which is
why these bugs survived 1,100+ unit and 850+ integration tests.

## 1. Every dashboard collapsed into one-column-wide strips (CSP blocks inline styles)

Reproduction (browser, `/dashboards/1`): each widget was ~59 px wide. Console: 48 × "Applying inline style
violates the following Content Security Policy directive 'style-src 'self' 'nonce-…''". The UI correctly
sends `style-src 'self' 'nonce-…'` (no `unsafe-inline`; pinned by `SecurityHeadersTests`), and the dashboard
grid (`DashboardView.razor`) and bar charts (`BarChart.razor`) positioned and sized themselves with
`style="…"` attributes, which the browser silently drops. Server-rendered HTML (what every test read) looked
fine.

RED — `Dashboards/CspInlineStyleTests` (new): 5 failed — inline style attribute on `/dashboards/4`,
`BarChart` output, `/audit`, and the stylesheet missing the placement classes (`ds-gc-1` …).

GREEN — placement is by class (`ds-gc-N` start column, `ds-gs-N` span, `ds-gr-N` row span) and bar width by
class (`ds-pct-N`, whole percent), all defined in `ds.css`; the strict CSP is unchanged. Re-check in the
browser: widgets fill the grid, bars and donut render.

## 2. Widgets ignored their row, so side-by-side widgets dropped to the next row

Reproduction: Network Overview's "Severity distribution" (columns 9–12, row 1) rendered below and to the
right of "Message rate" because `DashboardView` emitted column and span but never the row; CSS auto-placement
then pushed it down.

RED — `NetworkOverview_PlacesSeverityBesideMessageRate_OnTheFirstRow` and the `ds-gy-N` presence check
failed (`ds-gy-1` undefined, markup without a row class).

GREEN — `ds-gy-N` (start row, 1–30) emitted and defined. Browser: Severity sits beside Message rate.

## 3. Long tables rendered wrongly (framework `Virtualize` needs inline styles)

Reproduction: `/audit` header said "chain intact — 16 entries" and rendered no rows. `DataTable` used
`<Virtualize>`, whose scroll spacers carry inline `style` heights — blocked by the same CSP.

RED — `DesignSystem/DataTableWindowingTests` (new): the old component cannot even render without browser JS
(`Cannot provide a value for property 'JSRuntime' … Virtualize`) and, in a browser, emitted blocked styles.

GREEN — `DataTable` renders in windows of 100 rows with a "Show N more (M remaining)" control, no inline
style, same parameters (the 8 callers are unchanged). Short tables render every row.

## 4. "Recent criticals" listed informational events (and omitted the most severe)

Severity compares on the numeric syslog code (0 = most severe), so `severity:>=critical` means "critical or
LESS severe". The shipped widget, and the example text in the search bar and widget picker, used `>=`.

RED — `Dashboards/ShippedDashboardQueryTests` (new, unit): 6 of 7 failed (Emergency and Alert were
excluded; Error, Warning, Informational, Debug were included). `Search/SeverityOperatorHintTests` failed on
the `/search` page placeholder.

GREEN — widget query is `severity:<=critical`; examples read `severity:<=error`. System dashboards are
refreshed by the seeder's `ON CONFLICT … DO UPDATE` on every start, so upgraded installs get the fix.

## 5. Stale stylesheet after an upgrade

Reproduction: after the fix shipped, the browser kept the previous `ds.css` (no `Cache-Control`, ETag only —
heuristic caching for days) and rendered new markup against old CSS until a hard refresh.

RED — `Hardening/StaticAssetCachingTests` (new): 3 failed — no caching policy on `/ds.css`, `/app.css`,
`/js/app.js`.

GREEN — static files send `Cache-Control: no-cache` (always revalidate; a 304 is cheap). Verified in the
browser: the next load picked up the new stylesheet with no hard refresh.

## 6. Self-update could never report an update (version parser rejected the build metadata)

Reproduction (end-to-end rehearsal, see `verification.md`): with a trusted key and a correctly signed v1.1.4
release on a local fake GitHub, the Settings → Updates page said "Up to date". The running product reads its
own version back as `1.1.3+<commit sha>` (the SDK stamps the source commit as semver build metadata);
`ProductVersion.TryParse` stripped a `-` suffix but not `+metadata`, could not parse its own version, and
`IsNewer` answered "not newer" for every release. Every shipped build since v1.1.1 had this.

RED — `Updates/ProductVersionTests`: 3 failed (`1.1.3+<sha>` unparseable; `IsNewer("1.1.4","1.1.3+<sha>")`
false). Existing tests only used plain `1.1.3` strings.

GREEN — the suffix cut now stops at the first `-` or `+`. Added a guard that reads the version the built
assembly actually reports and requires it to parse. Re-run of the rehearsal: "Update ready — version 1.1.4.
Downloaded and verified".

## 7. Installer prerequisite check had no regression test (closes the TDD gap noted in the review)

`Installer/InstallerPrerequisiteCheckTests` (new, unit) pins the MSI `RegistrySearch` key to the native
`SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost` path, the launch condition
`Installed OR DOTNETSHAREDHOST`, and the bundle search to the same key with `Bitness="always64"`.

RED — the original `WOW6432Node` key put back into `installer/Product.wxs`:

```
Failed ...InstallerPrerequisiteCheckTests.MsiRegistrySearch_ReadsTheNativeSharedHostKey_NotTheWow6432NodeView
Expected string to be "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" with a length of 54, but
"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost" has a length of 66, differs near "WOW" (index 9).
```

GREEN — `installer/Product.wxs` restored to the committed file (`git diff` empty): 3 / 3.

## 8. A pending device was invisible on the dashboards

Reproduction: a source's first message creates a "pending device" (small red "1" in the navigation) that stays
out of Device Health until an Administrator approves it. Device Health read "No devices are visible to you
yet" and the first-run checklist step "A device has sent its first message" stayed unticked, while the Network
Overview filled with that device's messages.

RED — `Dashboards/PendingDeviceBannerTests`: no "waiting for your approval" text on any dashboard once a
device was pending (and, after the first version of the fix, a second red: the checklist rendered raw Razor
code — `message@if (!_hasDevice && _pending > 0)` — because `@if` was glued to the preceding text; caught by
looking at the page in a browser, then pinned by `NotContain("@if")`).

GREEN — every dashboard shows "N new device(s) waiting for your approval" with a "Review and approve" button
for Administrators and "Ask an Administrator to review them" for everyone else (they cannot open the page);
the checklist step explains itself ("— it is waiting for approval (see above)"). Seen in the browser.

## 9. Devices with no hostname showed as "(none)" / a dash

RFC 3164 messages often carry no hostname; "Top talkers" listed "(none)" as the busiest device and "Recent
criticals" showed a dash in the Host column.

RED — `Dashboards/HostFallbackTests`: 4 failed (grouping returned `(none)`; `RecentEventRow.HostLabel` with the
old behaviour returned null/blank for a missing hostname).

GREEN — grouping by `hostname` uses `COALESCE(NULLIF(TRIM(hostname),''), source_ip, '(none)')` and the recent-events
table labels a row with its source address when the hostname is missing. The existing hostname oracle tests
(`AggregationOracleTests`) still pass. Seen in the browser: "Top talkers" leads with `127.0.0.1`, not `(none)`.
Alert grouping (`SqliteAlertWindowReader` / `AlertGrouping`) was deliberately NOT changed: it has its own in-memory
equivalent that must stay identical.

## 10. Updates page: stale "Update ready" and an internal option name

Rehearsal findings (see `verification.md`): after a staged installer was discarded the page still said "Update
ready" with a download link (the download itself correctly returned 404); a later failed check was hidden behind
the earlier "ready" message; and a release without a manifest produced "has no ManifestAssetName asset".

RED — `Updates/UpdatePageStatusTests`: 3 failed.

GREEN — the status is corrected for what is on disk (a missing staged file is reported as "no longer on disk —
click Check now"), a later failure is shown next to a still-valid ready installer ("Last check failed: … the
installer above was verified earlier and is still valid"), and the missing-manifest message is plain English
("has no signed update manifest (update-manifest.json), so it cannot be verified and is not offered").

Not changed, noted: `UpdateCheckerTests` can only exercise the disabled and no-key paths, because the trust anchor
is baked in at build time and cannot be injected — which is how the version-parsing bug (§6) reached release. The
full verify-and-download path was covered here by the end-to-end rehearsal instead; making the key injectable for
tests is a worthwhile follow-up.
