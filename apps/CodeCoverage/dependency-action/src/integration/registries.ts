import * as fs from 'node:fs';
import * as http from 'node:http';
import type { AddressInfo } from 'node:net';
import * as path from 'node:path';
import { sha1, sha256hex, sha512Integrity, tgz, zip } from './archives';

/**
 * Local registries for the integration tests (§13.11.2). Each one serves only what the test put in it;
 * the public registries are never configured, so an accidental fetch fails instead of passing.
 */

export interface NpmPackage {
  name: string;
  version: string;
  dependencies?: Record<string, string>;
  peerDependencies?: Record<string, string>;
  scripts?: Record<string, string>;
}

export interface NpmRegistry {
  url: string;
  close(): Promise<void>;
}

/** A tiny npm registry (packuments + tarballs), enough for npm/pnpm/yarn lockfile-only resolution. */
export async function npmRegistry(packages: NpmPackage[]): Promise<NpmRegistry> {
  const tarballs = new Map<string, Buffer>();
  const byName = new Map<string, NpmPackage[]>();
  for (const p of packages) byName.set(p.name, [...(byName.get(p.name) ?? []), p]);
  const server = http.createServer((req, res) => {
    const url = decodeURIComponent((req.url ?? '').split('?')[0]).replace(/^\//, '');
    if (tarballs.has(url)) {
      res.writeHead(200, { 'content-type': 'application/octet-stream' });
      res.end(tarballs.get(url));
      return;
    }
    const versions = byName.get(url);
    if (!versions) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end('{"error":"not found"}');
      return;
    }
    const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
    const doc = {
      name: url,
      'dist-tags': { latest: versions[versions.length - 1].version },
      versions: Object.fromEntries(
        versions.map((p) => {
          const file = `${p.name}/-/${p.name.split('/').pop()}-${p.version}.tgz`;
          if (!tarballs.has(file)) {
            tarballs.set(file, tgz({ 'package/package.json': JSON.stringify({ name: p.name, version: p.version, dependencies: p.dependencies, peerDependencies: p.peerDependencies, scripts: p.scripts }) }));
          }
          const data = tarballs.get(file)!;
          return [p.version, { name: p.name, version: p.version, dependencies: p.dependencies, peerDependencies: p.peerDependencies, scripts: p.scripts, dist: { tarball: `${base}/${file}`, integrity: sha512Integrity(data), shasum: sha1(data) } }];
        }),
      ),
    };
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(doc));
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  return {
    url: `http://127.0.0.1:${(server.address() as AddressInfo).port}/`,
    close: () => new Promise((r) => server.close(() => r())),
  };
}

export interface NuGetPackage {
  id: string;
  version: string;
  dependencies?: Array<{ id: string; range: string }>;
  /** Extra files, e.g. build/<id>.targets (S14: must NOT run on restore). */
  files?: Record<string, string>;
}

/** A NuGet folder feed with hand-made .nupkg files (nuspec + optional files). */
export function nugetFeed(dir: string, packages: NuGetPackage[]): void {
  fs.mkdirSync(dir, { recursive: true });
  for (const p of packages) {
    const deps = (p.dependencies ?? []).map((d) => `<dependency id="${d.id}" version="${d.range}" />`).join('');
    const nuspec = `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>${p.id}</id><version>${p.version}</version><authors>test</authors><description>test</description>
    <dependencies><group targetFramework="net8.0">${deps}</group></dependencies>
  </metadata>
</package>`;
    fs.writeFileSync(path.join(dir, `${p.id}.${p.version}.nupkg`), zip({ [`${p.id}.nuspec`]: nuspec, ...(p.files ?? {}) }));
  }
}

export interface Wheel {
  name: string;
  version: string;
  requires?: string[];
}

/** A --find-links directory of pure-Python wheels. */
export function wheelDir(dir: string, wheels: Wheel[]): void {
  fs.mkdirSync(dir, { recursive: true });
  for (const w of wheels) {
    const dist = `${w.name.replace(/-/g, '_')}-${w.version}`;
    const metadata = `Metadata-Version: 2.1\nName: ${w.name}\nVersion: ${w.version}\n${(w.requires ?? []).map((r) => `Requires-Dist: ${r}\n`).join('')}`;
    const wheel = 'Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n';
    const files: Record<string, string> = {
      [`${w.name.replace(/-/g, '_')}/__init__.py`]: '',
      [`${dist}.dist-info/METADATA`]: metadata,
      [`${dist}.dist-info/WHEEL`]: wheel,
    };
    const record = Object.entries(files)
      .map(([f, c]) => `${f},sha256=${Buffer.from(sha256hex(Buffer.from(c)), 'hex').toString('base64url')},${Buffer.byteLength(c)}`)
      .concat([`${dist}.dist-info/RECORD,,`])
      .join('\n');
    files[`${dist}.dist-info/RECORD`] = record + '\n';
    fs.writeFileSync(path.join(dir, `${dist}-py3-none-any.whl`), zip(files));
  }
}

export interface GoModule {
  path: string;
  version: string;
  goVersion?: string;
}

/** A GOPROXY=file:// tree. */
export function goProxy(dir: string, modules: GoModule[]): void {
  const byPath = new Map<string, GoModule[]>();
  for (const m of modules) byPath.set(m.path, [...(byPath.get(m.path) ?? []), m]);
  for (const [modPath, versions] of byPath) {
    const base = path.join(dir, ...modPath.split('/'), '@v');
    fs.mkdirSync(base, { recursive: true });
    fs.writeFileSync(path.join(base, 'list'), versions.map((v) => v.version).join('\n') + '\n');
    for (const v of versions) {
      const mod = `module ${modPath}\n\ngo ${v.goVersion ?? '1.21'}\n`;
      fs.writeFileSync(path.join(base, `${v.version}.info`), JSON.stringify({ Version: v.version, Time: '2026-01-01T00:00:00Z' }));
      fs.writeFileSync(path.join(base, `${v.version}.mod`), mod);
      const prefix = `${modPath}@${v.version}/`;
      const name = modPath.split('/').pop()!.replace(/[^a-z0-9]/gi, '');
      fs.writeFileSync(path.join(base, `${v.version}.zip`), zip({ [`${prefix}go.mod`]: mod, [`${prefix}${name}.go`]: `package ${name}\n\nconst V = "${v.version}"\n` }));
    }
  }
}

export interface Crate {
  name: string;
  version: string;
  deps?: Array<{ name: string; req: string }>;
}

/** A Cargo local-registry (index + .crate files). */
export function cargoLocalRegistry(dir: string, crates: Crate[]): void {
  fs.mkdirSync(path.join(dir, 'index'), { recursive: true });
  const lines = new Map<string, string[]>();
  for (const c of crates) {
    const toml = `[package]\nname = "${c.name}"\nversion = "${c.version}"\nedition = "2021"\n\n[dependencies]\n${(c.deps ?? []).map((d) => `${d.name} = "${d.req}"`).join('\n')}\n`;
    const crate = tgz({ [`${c.name}-${c.version}/Cargo.toml`]: toml, [`${c.name}-${c.version}/src/lib.rs`]: '' });
    fs.writeFileSync(path.join(dir, `${c.name}-${c.version}.crate`), crate);
    const entry = {
      name: c.name, vers: c.version, cksum: sha256hex(crate), features: {}, yanked: false,
      deps: (c.deps ?? []).map((d) => ({ name: d.name, req: d.req, features: [], optional: false, default_features: true, target: null, kind: 'normal' })),
    };
    lines.set(c.name, [...(lines.get(c.name) ?? []), JSON.stringify(entry)]);
  }
  for (const [name, entries] of lines) {
    const n = name.toLowerCase();
    const rel = n.length <= 2 ? [`${n.length}`, n] : n.length === 3 ? ['3', n[0], n] : [n.slice(0, 2), n.slice(2, 4), n];
    const file = path.join(dir, 'index', ...rel);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, entries.join('\n') + '\n');
  }
}

export interface ComposerPackage {
  name: string;
  version: string;
  require?: Record<string, string>;
  type?: string;
  extra?: Record<string, unknown>;
}

/** A Composer `artifact` repository (one zip per version). */
export function composerArtifacts(dir: string, packages: ComposerPackage[]): void {
  fs.mkdirSync(dir, { recursive: true });
  for (const p of packages) {
    const composer = { name: p.name, version: p.version, require: p.require ?? {}, type: p.type ?? 'library', extra: p.extra ?? {} };
    fs.writeFileSync(path.join(dir, `${p.name.replace('/', '-')}-${p.version}.zip`), zip({ 'composer.json': JSON.stringify(composer) }));
  }
}

export interface MavenArtifact {
  group: string;
  artifact: string;
  version: string;
}

/** A file:// Maven repository with pom + empty jar per artifact. */
export function mavenRepo(dir: string, artifacts: MavenArtifact[]): void {
  for (const a of artifacts) {
    const base = path.join(dir, ...a.group.split('.'), a.artifact, a.version);
    fs.mkdirSync(base, { recursive: true });
    const pom = `<project xmlns="http://maven.apache.org/POM/4.0.0"><modelVersion>4.0.0</modelVersion><groupId>${a.group}</groupId><artifactId>${a.artifact}</artifactId><version>${a.version}</version><packaging>jar</packaging></project>\n`;
    fs.writeFileSync(path.join(base, `${a.artifact}-${a.version}.pom`), pom);
    fs.writeFileSync(path.join(base, `${a.artifact}-${a.version}.jar`), zip({ 'META-INF/MANIFEST.MF': 'Manifest-Version: 1.0\n' }));
  }
}
