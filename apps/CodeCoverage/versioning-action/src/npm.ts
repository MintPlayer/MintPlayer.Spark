import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { execOk, type Exec } from './exec';

export interface NpmPackage {
  name: string;
  /** The tracked package.json, absolute. */
  file: string;
  /** ng-packagr's output package.json (`<ng-package dest>/package.json`), absolute, when the build produced it. */
  builtFile: string | null;
  /** `"private": true`: never published, so never stamped. */
  private: boolean;
  version: string | null;
}

/** The version onboarding writes in place of a package's own (PRD D17); a package still at it is never published. */
export const PLACEHOLDER = '0.0.0-PLACEHOLDER';

/**
 * What the action writes into one package.json: the version, with the dist-tag `npm publish` uses by default
 * (`publishConfig.tag`) for a release, or `withhold` for a package that has no version to publish.
 */
export type Stamp = { version: string; distTag: string | null } | 'withhold';

// devDependencies stay as they are: npm drops them from a published package, and a workspace resolves them locally.
const DEPENDENCY_FIELDS = ['dependencies', 'peerDependencies', 'optionalDependencies'] as const;

/**
 * The repository's npm packages by name, from its tracked package.json files: untracked build output (ng-packagr's
 * dist, which holds a copy of the same name) and node_modules are never mistaken for a package.
 */
export async function discoverPackages(exec: Exec, cwd: string): Promise<Map<string, NpmPackage>> {
  const out = await execOk(exec, 'git', ['ls-files', '-z', '--', ':(glob)**/package.json'], { cwd });
  const packages = new Map<string, NpmPackage>();
  for (const relative of out.split('\0').filter((f) => f && !f.split('/').includes('node_modules'))) {
    const file = path.join(cwd, relative);
    const json = readJson(file);
    if (typeof json?.name !== 'string' || packages.has(json.name)) continue;
    packages.set(json.name, {
      name: json.name, file, builtFile: builtPackageJson(path.dirname(file), cwd),
      private: json.private === true, version: typeof json.version === 'string' ? json.version : null,
    });
  }
  return packages;
}

function builtPackageJson(directory: string, root: string): string | null {
  const ngPackage = readJson(path.join(directory, 'ng-package.json'));
  if (typeof ngPackage?.dest !== 'string') return null;
  const dest = path.resolve(directory, ngPackage.dest);
  const inside = path.relative(path.resolve(root), dest);
  if (inside.startsWith('..') || path.isAbsolute(inside)) return null;
  const file = path.join(dest, 'package.json');
  return existsSync(file) ? file : null;
}

function readJson(file: string): Record<string, any> | null {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

/**
 * Stamps each package into its package.json and the built copy (PRD D3, S2), and points its dependencies on the
 * repository's other packages at `^<version>`: the version this run released, else the package's last tag. A release
 * gets its dist-tag as `publishConfig.tag`; an unchanged package gets the version it already published, which the
 * publisher then skips; a withheld one gets `"private": true`, so no publisher ships its placeholder (D9). Edited as
 * JSON with the file's own indentation and trailing newline, so the diff is the versions alone.
 */
export function writeVersions(packages: Map<string, NpmPackage>, stamps: Map<string, Stamp>, internal: Map<string, string>): string[] {
  const written: string[] = [];
  for (const [name, stamp] of stamps) {
    const pkg = packages.get(name);
    if (!pkg) throw new Error(`The engine released ${name}, but no tracked package.json declares it.`);
    for (const file of [pkg.file, pkg.builtFile]) {
      if (!file) continue;
      rewrite(file, stamp, internal);
      written.push(file);
    }
  }
  return written;
}

function rewrite(file: string, stamp: Stamp, internal: Map<string, string>): void {
  const text = readFileSync(file, 'utf8');
  const json = JSON.parse(text);
  if (stamp === 'withhold') json.private = true;
  else {
    json.version = stamp.version;
    if (stamp.distTag) json.publishConfig = { ...(json.publishConfig ?? {}), tag: stamp.distTag };
  }
  for (const field of DEPENDENCY_FIELDS) {
    const deps = json[field];
    if (!deps || typeof deps !== 'object') continue;
    for (const dep of Object.keys(deps)) {
      const target = internal.get(dep);
      if (target !== undefined) deps[dep] = `^${target}`;
    }
  }
  const indent = /^[ \t]+(?=")/m.exec(text)?.[0] ?? '  ';
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  let out = JSON.stringify(json, null, indent).replace(/\n/g, eol);
  if (/\r?\n$/.test(text)) out += eol;
  writeFileSync(file, out);
}
