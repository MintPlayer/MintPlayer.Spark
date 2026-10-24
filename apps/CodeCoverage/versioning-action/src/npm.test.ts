import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { realExec } from './exec';
import { discoverPackages, writeVersions } from './npm';

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
  });

  it('writes the version and internal ranges, keeping the file\'s formatting', async () => {
    const original = '{\r\n    "name": "@acme/ui",\r\n    "version": "0.0.0",\r\n    "peerDependencies": {\r\n        "@acme/core": "*",\r\n        "@angular/core": "^22.0.0"\r\n    },\r\n    "devDependencies": {\r\n        "@acme/core": "*"\r\n    }\r\n}\r\n';
    const root = repo({ 'libs/ui/package.json': original, 'libs/core/package.json': '{ "name": "@acme/core" }' });
    const packages = await discoverPackages(realExec, root);

    writeVersions(packages, new Map([['@acme/ui', '2.1.0']]), new Map([['@acme/core', '1.4.0'], ['@acme/ui', '2.1.0']]));

    const written = readFileSync(path.join(root, 'libs/ui/package.json'), 'utf8');
    expect(written).toBe(original.replace('"0.0.0"', '"2.1.0"').replace('"@acme/core": "*",', '"@acme/core": "^1.4.0",'));
  });

  it('refuses a release no tracked package.json declares', async () => {
    const root = repo({ 'package.json': '{ "name": "root" }' });
    const packages = await discoverPackages(realExec, root);
    expect(() => writeVersions(packages, new Map([['@acme/ghost', '1.0.0']]), new Map())).toThrow(/@acme\/ghost/);
  });
});
