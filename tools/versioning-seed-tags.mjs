#!/usr/bin/env node
// Seed tags for Spark's adoption of package versioning (#153, docs/coverage_package_versioning_PRD.md D17).
//
// The versions now come from `<PackageId>/v<version>` tags, and the sources hold 0.0.0-PLACEHOLDER. Before the
// adoption merges, every published package needs a tag at the version it last shipped, or the engine treats it as
// new: a platform-anchored package with no tag computes `11.0.0` and loses its preview line. This script reads those
// versions from a ref that still holds them (master before the merge), and prints or creates one tag per package
// at that ref, so the tags are reachable from the merge commit.
//
//   node tools/versioning-seed-tags.mjs                 # print the tags it would create at master
//   node tools/versioning-seed-tags.mjs --ref <ref>     # read the versions from <ref> and tag it
//   node tools/versioning-seed-tags.mjs --create        # create the tags locally; push them yourself
//
// Pushing is deliberately not done here: the tags are public and permanent, so the owner pushes them.
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const args = process.argv.slice(2);
const ref = args.includes('--ref') ? args[args.indexOf('--ref') + 1] : 'master';
const create = args.includes('--create');

const git = (...a) => execFileSync('git', a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const element = (xml, name) => xml.match(new RegExp(`<${name}>\\s*([^<]*?)\\s*</${name}>`))?.[1];

const commit = git('rev-parse', '--verify', `${ref}^{commit}`).trim();
const files = git('ls-tree', '-r', '--name-only', commit).split('\n').filter(Boolean);

const seeds = [];
for (const file of files.filter((f) => f.startsWith('libs/') && f.endsWith('.csproj'))) {
  const xml = git('show', `${commit}:${file}`);
  const version = element(xml, 'Version');
  if (!version || element(xml, 'IsPackable') === 'false') continue;
  seeds.push({ id: element(xml, 'PackageId') ?? path.basename(file, '.csproj'), version, file });
}
for (const file of files.filter((f) => f.startsWith('libs/node_packages/') && /^libs\/node_packages\/[^/]+\/package\.json$/.test(f))) {
  const json = JSON.parse(git('show', `${commit}:${file}`));
  if (json.private === true || !json.name || !json.version) continue;
  seeds.push({ id: json.name, version: json.version, file });
}

const existing = new Set(git('tag', '--list').split('\n').filter(Boolean));
let created = 0;
for (const { id, version, file } of seeds.sort((a, b) => a.id.localeCompare(b.id))) {
  const tag = `${id}/v${version}`;
  if (version.startsWith('0.0.0')) throw new Error(`${file} at ${ref} holds ${version}: read the versions from a ref before the placeholders`);
  if (existing.has(tag)) { console.log(`exists  ${tag}`); continue; }
  if (create) { git('tag', tag, commit); created++; }
  console.log(`${create ? 'created' : 'would  '} ${tag}`);
}
console.log(`${seeds.length} packages at ${ref} (${commit.slice(0, 8)})${create ? `, ${created} tags created` : ''}.`);
if (create && created) console.log(`Push them with: git push origin ${'<tag>…'} (or --tags, if no other local tags are pending)`);
