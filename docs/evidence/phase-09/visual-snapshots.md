# Phase 9 — visual regression

The phase's Validation section: "snapshot each widget type against fixture data; assert
pixel-stable rendering across runs so a styling change cannot silently break a chart."

## Method

`bunit` is banned on this repo (it drags in `AngleSharp`, GHSA-pgww-w46g-26qg — the
`dev-vm-constraints` note). Instead `tests/…/TestSupport/RazorRenderer.cs` renders a
component to an HTML string with the ASP.NET shared framework's
`Microsoft.AspNetCore.Components.Web.HtmlRenderer` — no browser, no AngleSharp, no new
dependency. The eight presentational widget components are pure (parameters only), so they
render with a bare service provider.

`WidgetComponentTests.Widget_RendersPixelStable` renders each widget against a fixed
`AggregationResult` and compares the normalised HTML byte-for-byte against a committed
`tests/fixtures/dashboards/<name>.snapshot.html`. Normalisation collapses line endings and
**redacts the time-axis label text** (it renders bucket starts in the machine's local
timezone — correct for a user but not deterministic across machines, TESTING_STANDARDS
§2.3); the polyline / bar / arc geometry, which is what a styling regression would break,
stays byte-checked.

## Snapshots committed

| Widget | Snapshot |
|---|---|
| Line chart | `timeseries-line.snapshot.html` |
| Area chart | `timeseries-area.snapshot.html` |
| Bar chart | `bar.snapshot.html` |
| Top-N table | `topn.snapshot.html` |
| Severity donut | `donut.snapshot.html` |
| Counter (bytes) | `counter.snapshot.html` |
| Rate gauge | `gauge.snapshot.html` |

A change to any widget's markup or geometry fails the test with a diff until the snapshot is
updated deliberately.

## Empty / sparse / extreme data (`WidgetComponentTests`, 10 tests)

- Every widget type with **zero rows** renders its empty state, no exception (the phase's
  "every widget renders an empty state, never an exception").
- A **100,000-point** series still renders (`ChartGeometry.Decimate` down-samples to ≤ 240
  points, keeping the endpoints).
- **All-identical** values render a flat line; **negative / zero** values do not throw
  (`ChartGeometry` clamps to `[0, 1]` against a `NiceCeiling` axis).

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "WidgetComponentTests"
Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17
```
