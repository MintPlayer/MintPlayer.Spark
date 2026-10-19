#!/usr/bin/env node
// `node tools/seat-map-perf/build-harness.mjs <output.html>` — the S7 seat-map perf harness (commerce PRD §7, S7).
//
// Writes one self-contained HTML page that runs ng-spark's seat-map renderer (ticketing/venues/src/seat-map/*.ts)
// over a synthetic 50,000-seat stadium and measures frame times while it zooms and pans through every level of
// detail. No build tool and no app server: the TypeScript is type-stripped by Node itself (module.stripTypeScriptTypes,
// Node ≥ 23.2; the renderer is written in erasable syntax only for this), then inlined. Open the page in Chromium (the
// Playwright MCP server, or any browser); the results are in `window.__seatMapPerf` and on the page.
//
// Pass `?seats=N` (rounded to whole blocks of 1,250) to try another size.

import { readFileSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const source = join(root, 'libs/node_packages/ng-spark/ticketing/venues/src/seat-map');
const files = ['viewport.ts', 'seat-map-model.ts', 'seat-map-renderer.ts', 'seat-map-layer.ts', 'test-utils.ts'];

const code = files.map((file) => {
  const stripped = stripTypeScriptTypes(readFileSync(join(source, file), 'utf8'), { mode: 'strip' });
  return `// ---- ${file}\n` + stripped
    .replace(/^import[\s\S]*?from\s+'[^']+';\s*$/gm, '')
    .replace(/^export\s+(?=(const|function|let|class))/gm, '');
}).join('\n');

const bench = `
const params = new URLSearchParams(location.search);
const wanted = Number(params.get('seats') || 50000);
const blocks = Math.max(1, Math.round(wanted / 1250));
const blocksX = Math.ceil(Math.sqrt(blocks * 1.6));
const blocksY = Math.ceil(blocks / blocksX);
const canvas = document.getElementById('map');
const W = 1280, H = 800, dpr = window.devicePixelRatio || 1;
canvas.width = W * dpr; canvas.height = H * dpr;
canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
const ctx = canvas.getContext('2d');

const t0 = performance.now();
const payload = syntheticStadium({ blocksX, blocksY });
const t1 = performance.now();
const model = buildSeatMapModel(payload);
const t2 = performance.now();
applyStateFrame(model, { seatCount: model.count, states: encodeStates(syntheticStates(model.count)) });
const t3 = performance.now();
const direct = params.get('direct') === '1';
const layer = new SeatMapLayer();
const fit = fitViewport(payload.width, payload.height, W, H);
const options = { width: W, height: H, dpr, colors: DEFAULT_SEAT_MAP_COLORS, categoryColors: null, selected: new Uint8Array(model.count) };
for (let i = 0; i < model.count; i += 97) options.selected[i] = 1;

function scenario(name, frames, viewportAt) {
  return new Promise((done) => {
    const renders = [], intervals = [], lods = {};
    let rebuilds = 0;
    let frame = 0, last = 0;
    function step(now) {
      if (last) intervals.push(now - last);
      last = now;
      const vp = viewportAt(frame / Math.max(1, frames - 1));
      const a = performance.now();
      const stats = direct ? renderSeatMap(ctx, model, vp, options) : layer.draw(ctx, model, vp, options);
      if (stats.rebuilt) rebuilds++;
      renders.push(performance.now() - a);
      lods[stats.lod] = (lods[stats.lod] || 0) + 1;
      if (++frame < frames) requestAnimationFrame(step);
      else done({ ...summary(name, renders, intervals, lods), rebuilds });
    }
    requestAnimationFrame(step);
  });
}
function pct(values, p) { const s = [...values].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; }
function summary(name, renders, intervals, lods) {
  const mean = intervals.reduce((a, b) => a + b, 0) / Math.max(1, intervals.length);
  return {
    name,
    frames: renders.length,
    lods,
    renderMsMedian: +pct(renders, 0.5).toFixed(2),
    renderMsP95: +pct(renders, 0.95).toFixed(2),
    renderMsMax: +Math.max(...renders).toFixed(2),
    fpsFromRenderP95: +(1000 / Math.max(pct(renders, 0.95), 0.01)).toFixed(0),
    fpsMeasured: +(1000 / mean).toFixed(1),
  };
}
const scale = (px) => px / model.pitch;
const centreX = payload.width / 2, centreY = payload.height / 2;
(async () => {
  const results = [];
  results.push(await scenario('fit, pan (blocks LOD)', 120, (t) => panBy(fit, Math.sin(t * 6.28) * 40, 0)));
  results.push(await scenario('zoom sweep fit -> 30 px/seat -> fit', 240, (t) => {
    const k = t < 0.5 ? t * 2 : (1 - t) * 2;
    const s = fit.scale * Math.pow(scale(30) / fit.scale, k);
    return centreOn(centreX, centreY, s, W, H);
  }));
  results.push(await scenario('pan, 4 px/seat (dots, every seat on screen)', 180, (t) => centreOn(centreX + Math.sin(t * 6.28) * 200, centreY, Math.max(scale(4), fit.scale), W, H)));
  results.push(await scenario('pan, 6 px/seat (dots)', 180, (t) => centreOn(centreX + (t - 0.5) * payload.width * 0.5, centreY, scale(6), W, H)));
  results.push(await scenario('pan, 12 px/seat (circles + row labels)', 180, (t) => centreOn(centreX + (t - 0.5) * payload.width * 0.5, centreY, scale(12), W, H)));
  results.push(await scenario('pan, 24 px/seat (circles + seat labels)', 180, (t) => centreOn(centreX + (t - 0.5) * payload.width * 0.3, centreY, scale(24), W, H)));
  const report = {
    mode: direct ? 'direct' : 'layer cache', seats: model.count, blocks: payload.blocks.length, canvas: W + 'x' + H + ' @' + dpr + 'x', userAgent: navigator.userAgent,
    payloadMs: +(t1 - t0).toFixed(1), buildModelMs: +(t2 - t1).toFixed(1), applyStatesMs: +(t3 - t2).toFixed(1),
    results,
  };
  window.__seatMapPerf = report;
  document.getElementById('out').textContent = JSON.stringify(report, null, 2);
})();
`;

const html = `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>S7 seat-map perf harness</title>
<style>body{font:13px system-ui,sans-serif;margin:16px}canvas{border:1px solid #ccc;display:block}pre{white-space:pre-wrap}</style></head>
<body>
<h1>S7 seat-map perf harness</h1>
<p>ng-spark <code>ticketing/venues</code> renderer, synthetic stadium. Generated by <code>tools/seat-map-perf/build-harness.mjs</code>.</p>
<canvas id="map"></canvas>
<pre id="out">running…</pre>
<script type="module">
${code}
${bench}
</script>
</body>
</html>
`;

const output = resolve(process.argv[2] ?? join(root, 'seat-map-harness.html'));
writeFileSync(output, html);
console.log(`wrote ${output}`);
