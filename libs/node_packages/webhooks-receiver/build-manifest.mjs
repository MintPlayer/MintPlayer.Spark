// Writes the published package.json and README into dist/, the folder the publish step pushes. The source package.json
// carries no entry points: they are relative to dist/, like ng-packagr's output. The versioning action stamps the
// released version into this copy afterwards (the Nx build target's outputPath tells it where the copy is).
import { copyFileSync, readFileSync, writeFileSync } from 'node:fs';

const manifest = JSON.parse(readFileSync('package.json', 'utf8'));
delete manifest.scripts;
manifest.main = './index.js';
manifest.types = './index.d.ts';
manifest.exports = { '.': { types: './index.d.ts', default: './index.js' }, './package.json': './package.json' };
writeFileSync('dist/package.json', JSON.stringify(manifest, null, 2) + '\n');
copyFileSync('README.md', 'dist/README.md');
