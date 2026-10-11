# Score renderers: gauge, threshold bar, trend and delta

Scores, percentages and their history are drawn by four attribute renderers in ng-spark's
`@mintplayer/ng-spark/score-renderers` entry point. Each draws a grid cell and a detail value, and is
configured per attribute through the model's `rendererOptions`, so two apps that show a score differently
differ only in configuration.

| Renderer | Draws | Value |
|---|---|---|
| `spark-gauge` | A ring filled to the score's share of the scale, the score inside, an optional grade beside it | a number |
| `spark-threshold-bar` | A compact progress bar coloured by band, the value next to it | a number |
| `spark-trend` | A sparkline in a cell, or an interactive trend chart | an array of numbers, or of `{ x, y }` points |
| `spark-delta` | A signed change, green when it improves and red when it worsens | a number |

They were generalised from CodeCoverage's coverage bar, sparkline, "coverage over time" chart and Δ
column, which now draw through them with identical output. Decisions and evidence:
[seo_analyzer_PRD.md](seo_analyzer_PRD.md) X5.

**No value never draws as zero.** `null`, `undefined`, `''` and anything that does not parse as a number
draw a muted dash (`—`), while a real `0` draws a 0% bar, an empty ring or `0.0`. A trend with fewer
points than `minPoints` (default 2) is a dash too: one point is not a trend.

## Setup

```ts
import { provideSparkScoreRenderers } from '@mintplayer/ng-spark/score-renderers';

providers: [
  provideSparkScoreRenderers([
    // the app's own renderers, as you would pass them to provideSparkAttributeRenderers
  ]),
]
```

`provideSparkScoreRenderers` **replaces** `provideSparkAttributeRenderers`. Do not use both: they provide
the same renderer list and the last one wins. The app's registrations come first, so an app renderer with
the same name replaces a score renderer. `sparkScoreRenderers` is the bare registration list, for an app
that composes its list itself.

Then name the renderer on the attribute in the model JSON:

```json
{
  "name": "Score",
  "dataType": "decimal",
  "renderer": "spark-gauge",
  "rendererOptions": { "bands": "letter-grades" }
}
```

A component can also be used directly in a template: `<spark-threshold-bar [value]="percent" [options]="…" />`.
That is how an app adapts a value that is not already a number (see the CodeCoverage example).

| Selector | Component | Note |
|---|---|---|
| `spark-gauge` | `SparkGaugeComponent` | |
| `spark-threshold-bar` | `SparkThresholdBarComponent` | |
| `spark-trend` | `SparkTrendComponent` | the registered renderer; loads both charts |
| `spark-trend-sparkline` | `SparkTrendSparklineComponent` | the sparkline alone |
| `spark-trend-chart` | `SparkTrendChartComponent` | the chart alone |
| `spark-delta` | `SparkDeltaComponent` | |

**Bundle size.** `provideSparkScoreRenderers` puts all four renderers, both chart wrappers included, in the
bundle of whatever provides it, usually the initial one. An app that draws only a sparkline in a cell and
keeps its chart on a lazily loaded page registers its own names with the half it needs
(`SparkTrendSparklineComponent`) through `provideSparkAttributeRenderers`, as CodeCoverage does.

## Bands and grades

The gauge and the threshold bar colour by **bands**. A band applies to a value `>= min`; the band without
a `min` is the floor. The highest `min` the value reaches wins, so the order of the list does not matter.
The colour comes from the unrounded value: 59.95 shows as "60.0%" and is still below a 60 band.

```json
"bands": [{ "min": 80, "color": "success" }, { "min": 60, "color": "warning" }, { "color": "danger" }]
```

Colours are Bootstrap theme names: `primary`, `secondary`, `success`, `danger`, `warning`, `info`,
`light`, `dark`. A band's `label` is a grade, which the gauge shows beside the ring. Two presets can be
named from JSON:

| Preset | Bands | Exported as |
|---|---|---|
| `"traffic-light"` (default) | green ≥ 80, amber ≥ 60, red below | `sparkTrafficLightBands` |
| `"letter-grades"` | A ≥ 90, B ≥ 80 (green), C ≥ 70, D ≥ 50 (amber), F below (red) | `sparkLetterGrades` |

`sparkScoreBand(value, bands)` and `sparkScoreColor(value, bands)` give the band and its ng-bootstrap
`Color` in code.

## Options

Every option is optional. An option of the wrong type is ignored and its default used.

### `spark-gauge`

| Option | Default | Meaning |
|---|---|---|
| `min`, `max` | `0`, `100` | The scale. The ring is clamped to it. |
| `bands` | `"traffic-light"` | Ring colour; with labels, the grade. |
| `decimals` | `0` | Decimals of the score in the ring. |
| `suffix` | none | Text after the score. |
| `label` | `"Score"` | Accessible name. The gauge is an ARIA `meter` whose value text includes the grade: "72 (C)". |
| `size` | `48` | Diameter in CSS pixels. |

### `spark-threshold-bar`

| Option | Default | Meaning |
|---|---|---|
| `min`, `max` | `0`, `100` | The scale. |
| `bands` | `"traffic-light"` | Bar colour. |
| `decimals` | `1` | Decimals of the shown value. |
| `suffix` | `"%"` | Text after the shown value. |
| `label` | `"Value"` | The bar's accessible name. |
| `width`, `height` | `"80px"`, `"8px"` | CSS size of the bar. |
| `showValue` | `true` | Show the value as text next to the bar. |

### `spark-trend`

| Option | Default | Meaning |
|---|---|---|
| `display` | `"sparkline"` | `"chart"` draws an interactive trend chart with axes instead. |
| `yMin`, `yMax` | fitted | A fixed scale, e.g. 0 and 100 for a percentage. |
| `label` | none | Accessible name. |
| `minPoints` | `2` | Fewer points draw the dash. |
| `width`, `height` | `"90px"`, `"24px"` | Sparkline CSS size. |
| `goal`, `goalLabel` | none | Chart only: a goal line. |
| `seriesId`, `seriesLabel` | `"value"`, `label` | Chart only: the one series' id and legend label. |

The value is an array. A number is a point at its index (`null` is a gap); a `{ x, y }` point keeps its
`x`, which may be a number or a `Date`. Anything else in the array makes the whole value "not a series".

### `spark-delta`

| Option | Default | Meaning |
|---|---|---|
| `decimals` | `1` | Decimals of the change. |
| `higherIsBetter` | `true` | `false` turns a rise red and a drop green (a cost, an error count, a load time). The sign never flips. |
| `suffix` | none | Text after the number, e.g. `" pt"`. |
| `empty` | `"—"` | Text for "nothing to compare". |

Direction and colour come from the unrounded change: +0.04 shows `+0.0` in green. An exact zero is a real
comparison and shows `0.0`, muted. `formatSparkDelta(value, options)` returns the same text and tone in
code.

## Example: CodeCoverage

CodeCoverage keeps its model renderer names (`coverage-bar`, `coverage-sparkline`, `coverage-delta`) and
draws them through the score renderers, so its App_Data model did not change. It keeps
`provideSparkAttributeRenderers` (its list is `codeCoverageRenderers` in `src/app/spark/renderers.ts`): it
names no `spark-*` renderer, and registering them would move the trend chart, which only its lazily
loaded detail page draws, into the initial bundle.

- **`coverage-delta`** is `SparkDeltaComponent` registered under the app's name; its defaults are exactly
  the coverage Δ.
- **`coverage-bar`**: the value is a nested `CoverageSummary`, not a number, so the app's `CoverageBarComponent`
  computes the percentage and draws `<spark-threshold-bar>` with its options spelled out:

  ```ts
  export const COVERAGE_BAR_OPTIONS: SparkThresholdBarOptions = {
    min: 0, max: 100, bands: sparkTrafficLightBands, decimals: 1, suffix: '%',
    label: 'Coverage', width: '80px', height: '8px', showValue: true,
  };
  ```
- **`coverage-sparkline`**: the value is the repository's full name; the app fetches its owner's
  sparklines and draws `<spark-trend-sparkline>` with `{ yMin: 0, yMax: 100, width: '90px', height: '24px', label: 'Coverage trend for owner/repo' }`.
- **The "coverage over time" card** draws `<spark-trend-chart>` with
  `{ yMin: 0, yMax: 100, goal: 80, goalLabel: '80% goal', seriesId: 'coverage', seriesLabel: 'Line coverage %' }`.

Spelling the options out, even where they equal the defaults, keeps a later change of a default from
changing a production page.

## Example: a 0–100 score with a grade (SeoTester)

```json
{ "name": "Score", "renderer": "spark-gauge", "rendererOptions": { "bands": "letter-grades", "label": "SEO score" } },
{ "name": "PerformanceScore", "renderer": "spark-threshold-bar", "rendererOptions": { "bands": "letter-grades", "decimals": 0, "suffix": "", "label": "Performance" } },
{ "name": "ScoreHistory", "renderer": "spark-trend", "rendererOptions": { "yMin": 0, "yMax": 100, "label": "Score trend" } },
{ "name": "ScoreChange", "renderer": "spark-delta", "rendererOptions": { "decimals": 0, "suffix": " pt" } }
```

A score of 72 draws a ring 72% full in amber with "72" inside and a "C" badge beside it; a category of 91
draws a green bar with "91"; a change of −4 draws a red `-4 pt`. A load time would use
`"higherIsBetter": false` on its delta.

## Testing

`libs/node_packages/ng-spark/score-renderers/src/score-renderers.spec.ts` covers bands and grades at their
edges, every option and the "no value is not zero" rule.

The chart wrappers (`bs-sparkline`, `bs-trend-chart`) host Lit elements that jsdom cannot; a spec swaps
in stubs with the same selector and inputs, on the component that imports each:
`TestBed.overrideComponent(SparkTrendSparklineComponent, { remove: { imports: [BsSparklineComponent] }, add: { imports: [StubSparkline] } })`
and likewise `SparkTrendChartComponent` with `BsTrendChartComponent` (CodeCoverage: `stubSparkTrendCharts()` in
`src/testing/test-utils.ts`).

CodeCoverage's switch is pinned by
`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/spark/score-renderers.characterisation.spec.ts`: written
against the app's own components before the switch, it renders whatever the app registers under each
renderer name and compares the DOM that reaches the screen (component hosts transparent, classes, inline
styles and aria attributes exact). It passed unchanged after the switch, and fails when the configuration
is wrong: dropping `label: 'Coverage'` fails 9 of its 46 cases, swapping the bands for `letter-grades`
fails the 59.95% edge.
