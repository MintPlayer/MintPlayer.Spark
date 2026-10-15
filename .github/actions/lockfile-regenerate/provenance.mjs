#!/usr/bin/env node
// The provenance check of the lockfile-regenerate action (README.md beside this file, "Provenance").
//
// The regenerate run executes the pull request's code (package-manager config, MSBuild, Yarn plugins), so a lockfile
// it writes is not what the reviewers approved. Every resolved package of the regenerated lockfile must therefore be
// one that the base branch's lockfile or the pull request head's lockfile already holds, identity and all:
//   npm   packages[*] (or v1 dependencies): name, version, resolved, integrity
//   pnpm  packages keys + resolution (integrity, tarball, repo/commit)
//   yarn  v1: name, version, resolved, integrity; Berry: resolution, checksum
//   nuget dependencies[tfm][name]: name, resolved, contentHash (project references skipped)
//
// Without the pull request head's lockfile (--head omitted) it falls back to a host allowlist: a package that the base
// does not hold must come from a host the base's lockfile already uses. NuGet lockfiles record no source, so there the
// fallback accepts only what the base holds.
//
// usage: node provenance.mjs --file <regenerated> --base <base branch's lockfile> [--head <PR head's lockfile>]
//                            [--label <path shown in messages>]
//   A missing or empty --base/--head file holds no packages. Omitting --head selects the fallback.
// Exit codes: 0 every package accounted for, 1 a violation (listed as name@version (host)), 2 usage or parse error.
// No dependencies; deterministic (sorted output).

import { existsSync, readFileSync } from 'node:fs';
import { basename } from 'node:path';

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i];
    if (!['--file', '--base', '--head', '--label'].includes(key) || i + 1 >= argv.length)
      throw new Error(`unexpected argument '${key}'`);
    out[key.slice(2)] = argv[i + 1];
  }
  if (!out.file || out.base === undefined) throw new Error('--file and --base are required');
  return out;
}

function kindOf(path) {
  switch (basename(path)) {
    case 'package-lock.json': case 'npm-shrinkwrap.json': return 'npm';
    case 'pnpm-lock.yaml': return 'pnpm';
    case 'yarn.lock': return 'yarn';
    case 'packages.lock.json': return 'nuget';
    default: throw new Error(`not a lockfile this check knows: ${path}`);
  }
}

/** The source host of a resolved URL or spec: `registry.npmjs.org`, `github.com`, or a protocol such as `file:`. */
export function hostOf(spec) {
  if (!spec) return 'none';
  const url = /^[a-z][a-z0-9+.-]*:\/\/(?:[^@/]*@)?([^/:?#]+)/i.exec(spec);
  if (url) return url[1].toLowerCase();
  const scp = /^[^@/:]+@([^/:]+):/.exec(spec); // git@github.com:owner/repo
  if (scp) return scp[1].toLowerCase();
  const protocol = /^([a-z][a-z0-9+.-]*):/i.exec(spec);
  if (protocol) return `${protocol[1].toLowerCase()}:`;
  return 'local';
}

const tuple = (label, host, ...identity) => ({ id: JSON.stringify(identity.map(v => v ?? '')), label, host });

function npmTuples(text) {
  const lock = JSON.parse(text);
  const out = [];
  if (lock.packages && typeof lock.packages === 'object') {
    for (const [key, p] of Object.entries(lock.packages)) {
      // The root, workspace links and local folders come from no source: nothing to account for.
      if (key === '' || !p || p.link || (p.resolved === undefined && p.integrity === undefined)) continue;
      const at = key.lastIndexOf('node_modules/');
      const name = p.name ?? (at < 0 ? key : key.slice(at + 'node_modules/'.length));
      out.push(tuple(`${name}@${p.version ?? '?'}`, hostOf(p.resolved), name, p.version, p.resolved, p.integrity));
    }
  } else {
    // lockfileVersion 1: nested dependencies.
    const walk = deps => {
      for (const [name, d] of Object.entries(deps ?? {})) {
        if (!d) continue;
        if (d.resolved !== undefined || d.integrity !== undefined)
          out.push(tuple(`${name}@${d.version ?? '?'}`, hostOf(d.resolved), name, d.version, d.resolved, d.integrity));
        walk(d.dependencies);
      }
    };
    walk(lock.dependencies);
  }
  return out;
}

const unquote = s => {
  s = s.trim();
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith('\'') && s.endsWith('\''))) return s.slice(1, -1);
  return s;
};

/** A one-line YAML flow mapping as pnpm writes it: `{integrity: sha512-..., tarball: https://...}`. */
function flowMap(body) {
  const map = {};
  for (const part of body.split(/,\s*/)) {
    const at = part.indexOf(':');
    if (at > 0) map[part.slice(0, at).trim()] = unquote(part.slice(at + 1));
  }
  return map;
}

function pnpmTuples(text) {
  const out = [];
  const entries = [];
  let inPackages = false;
  let current = null;
  let blockResolution = false;
  for (const line of text.split(/\r?\n/)) {
    if (/^\S/.test(line)) { inPackages = /^packages:\s*$/.test(line); current = null; continue; }
    if (!inPackages || line.trim() === '') continue;
    let m = /^ {2}(\S.*?):\s*(\{\})?\s*$/.exec(line);
    if (m) { current = { key: unquote(m[1]), resolution: null }; entries.push(current); blockResolution = false; continue; }
    if (!current) continue;
    m = /^ {4}resolution:\s*\{(.*)\}\s*$/.exec(line);
    if (m) { current.resolution = flowMap(m[1]); blockResolution = false; continue; }
    if (/^ {4}resolution:\s*$/.test(line)) { current.resolution = {}; blockResolution = true; continue; }
    m = /^ {6}([\w-]+):\s*(.*)$/.exec(line);
    if (blockResolution && m) { current.resolution[m[1]] = unquote(m[2]); continue; }
    if (/^ {4}\S/.test(line)) blockResolution = false;
  }
  for (const { key, resolution } of entries) {
    const r = resolution ?? {};
    if (r.directory || r.type === 'directory') continue; // a workspace or local folder
    // `/name@1.0.0(peer@2.0.0)` (v6) and `name@1.0.0` (v9) both label as name@1.0.0.
    const label = key.replace(/^\//, '').replace(/\(.*$/, '');
    const source = r.tarball ?? r.repo ?? '';
    out.push(tuple(label, source ? hostOf(source) : 'default registry', label, r.integrity, r.tarball, r.repo, r.commit));
  }
  return out;
}

/** The package name of a descriptor: `@scope/name@^1.0.0` → `@scope/name`. */
const nameOf = descriptor => {
  const at = descriptor.lastIndexOf('@');
  return at > 0 ? descriptor.slice(0, at) : descriptor;
};

function yarnTuples(text) {
  const berry = /^__metadata:/m.test(text);
  const blocks = [];
  let current = null;
  for (const line of text.split(/\r?\n/)) {
    if (line.startsWith('#') || line.trim() === '') continue;
    if (/^\S/.test(line)) { current = { header: line.replace(/:\s*$/, ''), fields: {} }; blocks.push(current); continue; }
    const m = /^ {2}([A-Za-z][\w-]*):?(?:\s+(.*))?$/.exec(line);
    if (m && current) current.fields[m[1]] = unquote(m[2] ?? '');
  }
  const out = [];
  for (const { header, fields } of blocks) {
    if (berry) {
      if (header.startsWith('__metadata')) continue;
      const resolution = fields.resolution ?? '';
      const spec = resolution.slice(nameOf(resolution).length + 1);
      if (!fields.checksum && /^(workspace|link|portal):/.test(spec)) continue;
      const protocol = /^([a-z]+):/.exec(spec);
      const host = /^[a-z][a-z0-9+.-]*:\/\//i.test(spec) ? hostOf(spec) : protocol ? `${protocol[1]}:` : 'none';
      out.push(tuple(resolution || header, host, resolution, fields.checksum));
    } else {
      if (fields.resolved === undefined && fields.integrity === undefined) continue;
      const name = nameOf(unquote(header.split(/,\s*/)[0]));
      out.push(tuple(`${name}@${fields.version ?? '?'}`, hostOf(fields.resolved), name, fields.version, fields.resolved, fields.integrity));
    }
  }
  return out;
}

function nugetTuples(text) {
  const lock = JSON.parse(text);
  const out = [];
  for (const deps of Object.values(lock.dependencies ?? {})) {
    for (const [name, d] of Object.entries(deps ?? {})) {
      if (!d || d.type === 'Project') continue;
      out.push(tuple(`${name}@${d.resolved ?? '?'}`, 'NuGet feed', name.toLowerCase(), d.resolved, d.contentHash));
    }
  }
  return out;
}

const parsers = { npm: npmTuples, pnpm: pnpmTuples, yarn: yarnTuples, nuget: nugetTuples };

function read(kind, path) {
  if (!path || !existsSync(path)) return [];
  const text = readFileSync(path, 'utf8').replace(/^﻿/, '');
  if (text.trim() === '') return [];
  if (/^(<{7}|={7}|>{7}|\|{7})/m.test(text)) throw new Error(`${path} still holds conflict markers`);
  return parsers[kind](text);
}

/** The check itself, exported for tests: returns the offending labels, sorted and de-duplicated. */
export function check(kind, file, base, head) {
  const allowed = new Set([...base, ...(head ?? [])].map(t => t.id));
  const unknown = file.filter(t => !allowed.has(t.id));
  if (head) return { violations: unknown, viaHost: [] };
  const hosts = new Set(base.map(t => t.host));
  const viaHost = kind === 'nuget' ? [] : unknown.filter(t => hosts.has(t.host));
  return { violations: unknown.filter(t => !viaHost.includes(t)), viaHost };
}

const show = list => [...new Set(list.map(t => `${t.label} (${t.host})`))].sort();

function main() {
  let opts;
  let kind;
  let file, base, head;
  try {
    opts = parseArgs(process.argv.slice(2));
    kind = kindOf(opts.file);
    file = read(kind, opts.file);
    base = read(kind, opts.base);
    head = opts.head === undefined ? undefined : read(kind, opts.head);
  } catch (e) {
    console.error(`::error::provenance: ${e.message}`);
    process.exit(2);
  }
  const label = opts.label ?? opts.file;
  const mode = head ? 'the base branch or the pull request head' : 'the base branch (fallback: no pull request head, host allowlist)';
  const { violations, viaHost } = check(kind, file, base, head);
  if (violations.length > 0) {
    const listed = show(violations);
    console.error(`::error::provenance: ${label} resolves ${listed.length} package(s) that ${mode} does not hold. The regenerate run executed the pull request's code, so a package neither side's lockfile resolved is refused:`);
    for (const line of listed) console.error(`  ${line}`);
    process.exit(1);
  }
  console.log(`provenance: ${label}: ${file.length} package(s), every one held by ${mode}.`);
  if (viaHost.length > 0) {
    console.log(`::warning::provenance: ${label}: ${show(viaHost).length} package(s) not in the base branch's lockfile, accepted by host allowlist only:`);
    for (const line of show(viaHost)) console.log(`  ${line}`);
  }
}

if (process.argv[1] && import.meta.url.endsWith(basename(process.argv[1]))) main();
