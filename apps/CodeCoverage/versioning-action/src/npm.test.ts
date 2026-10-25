import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { realExec } from './exec';
import { discoverPackages, PLACEHOLDER, writeVersions, type Stamp } from './npm';

function repo(files: Record<string, string>): string {
  const root = mkdtempSync(path.join(os.tmpdir(), 'versioning-npm-'));
  for (const [file, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  }
  execFileSync('git', ['init', '-q'], { cwd: root });
  execFileSync('git', ['add', '.'], { cwd: root });
  return root;
}

describe('npm versions', () => {
  it('finds tracked packages only, and the ng-packagr copy beside them', async () => {
    const root = repo({
      'package.json': '{ "name": "root", "private": true }\n',
      'libs/core/package.json': '{ "name": "@acme/core", "version": "0.0.0" }\n',
      'libs/core/ng-package.json': '{ "dest": "../../dist/core" }\n',
      'libs/core/node_modules/x/package.json': '{ "name": "x" }\n',
    });
    // Build output, untracked, as ng-packagr leaves it.
    mkdirSync(path.join(root, 'dist/core'), { recursive: true });
    writeFileSync(path.join(root, 'dist/core/package.json'), '{ "name": "@acme/core", "version": "0.0.0" }\n');

    const packages = await discoverPackages(realExec, root);

    expect([...packages.keys()].sort()).toEqual(['@acme/core', 'root']);
    expect(packages.get('@acme/core')!.builtFile).toBe(path.join(root, 'dist/core/package.json'));
    expect(packages.get('root')).toMatchObject({ private: true, version: null });
    expect(packages.get('@acme/core')).toMatchObject({ private: false, version: '0.0.0' });
  });

  // mintplayer-ng-bootstrap #446: @nx/js:tsc and @nx/vite:build copy package.json into outputPath, which publishes.
  it('finds the copy in an Nx build target\'s outputPath, and stamps it', async () => {
    const root = repo({
      'libs/tsc/package.json': '{ "name": "@acme/tsc", "version": "0.0.0-PLACEHOLDER" }\n',
      'libs/tsc/project.json': '{ "targets": { "build": { "executor": "@nx/js:tsc", "options": { "outputPath": "dist/libs/tsc" } } } }\n',
      'libs/vite/package.json': '{ "name": "@acme/vite", "version": "0.0.0-PLACEHOLDER" }\n',
      'libs/vite/project.json': '{ "targets": { "build": { "options": { "outputPath": "{workspaceRoot}/dist/{projectRoot}" } } } }\n',
      'libs/out/package.json': '{ "name": "@acme/out", "version": "0.0.0-PLACEHOLDER" }\n',
      'libs/out/project.json': '{ "targets": { "build": { "options": { "outputPath": "../outside" } } } }\n',
    });
    for (const dir of ['dist/libs/tsc', 'dist/libs/vite']) {
      mkdirSync(path.join(root, dir), { recursive: true });
      writeFileSync(path.join(root, dir, 'package.json'), '{ "name": "x", "version": "0.0.0-PLACEHOLDER" }\n');
    }

    const packages = await discoverPackages(realExec, root);
    expect(packages.get('@acme/tsc')!.builtFile).toBe(path.join(root, 'dist/libs/tsc/package.json'));
    expect(packages.get('@acme/vite')!.builtFile).toBe(path.join(root, 'dist/libs/vite/package.json'));
    expect(packages.get('@acme/out')!.builtFile).toBeNull();

    writeVersions(packages, new Map<string, Stamp>([['@acme/tsc', { version: '1.7.1', distTag: null }]]), new Map());
    expect(JSON.parse(readFileSync(path.join(root, 'dist/libs/tsc/package.json'), 'utf8')).version).toBe('1.7.1');
  });

  it('puts a release under its dist-tag, keeps the other publishConfig, and withholds a placeholder', async () => {
    const root = repo({
      'libs/ui/package.json': '{\n  "name": "@acme/ui",\n  "version": "0.0.0-PLACEHOLDER",\n  "publishConfig": { "access": "public" }\n}\n',
      'libs/new/package.json': '{\n  "name": "@acme/new",\n  "version": "0.0.0-PLACEHOLDER"\n}\n',
    });
    const packages = await discoverPackages(realExec, root);

    writeVersions(packages, new Map<string, Stamp>([['@acme/ui', { version: '3.0.0-rc.1', distTag: 'next' }], ['@acme/new', 'withhold']]), new Map());

    expect(JSON.parse(readFileSync(path.join(root, 'libs/ui/package.json'), 'utf8')))
      .toEqual({ name: '@acme/ui', version: '3.0.0-rc.1', publishConfig: { access: 'public', tag: 'next' } });
    expect(JSON.parse(readFileSync(path.join(root, 'libs/new/package.json'), 'utf8')))
      .toEqual({ name: '@acme/new', version: PLACEHOLDER, private: true });
  });

  it('writes the version and internal ranges, keeping the file\'s formatting', async () => {
    const original = '{\r\n    "name": "@acme/ui",\r\n    "version": "0.0.0",\r\n    "peerDependencies": {\r\n        "@acme/core": "*",\r\n        "@angular/core": "^22.0.0"\r\n    },\r\n    "devDependencies": {\r\n        "@acme/core": "*"\r\n    }\r\n}\r\n';
    const root = repo({ 'libs/ui/package.json': original, 'libs/core/package.json': '{ "name": "@acme/core" }' });
    const packages = await discoverPackages(realExec, root);

    writeVersions(packages, new Map<string, Stamp>([['@acme/ui', { version: '2.1.0', distTag: null }]]), new Map([['@acme/core', '1.4.0'], ['@acme/ui', '2.1.0']]));

    const written = readFileSync(path.join(root, 'libs/ui/package.json'), 'utf8');
    expect(written).toBe(original.replace('"0.0.0"', '"2.1.0"').replace('"@acme/core": "*",', '"@acme/core": "^1.4.0",'));
  });

  it('refuses a release no tracked package.json declares', async () => {
    const root = repo({ 'package.json': '{ "name": "root" }' });
    const packages = await discoverPackages(realExec, root);
    expect(() => writeVersions(packages, new Map<string, Stamp>([['@acme/ghost', { version: '1.0.0', distTag: null }]]), new Map())).toThrow(/@acme\/ghost/);
  });
});
