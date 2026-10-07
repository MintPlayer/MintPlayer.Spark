#!/usr/bin/env node
// `npm run stamp:library-model-ids -- <library project folder> [...]` — writes the ids of a library's
// shipped model files (composition PRD D5).
//
// A library's model ids are UUIDv5 over its SparkLibraryAlias and the element's names, never minted:
//   persistentObject.id        <- "{alias}:{Type}"
//   attributes/tabs/groups[].id <- "{alias}:{Type}.{attributes|tabs|groups}.{Name}"
//   queries[].id                <- "{alias}:{Type}.queries.{Name}"
// in the namespace 036a66ee-1790-46de-9f0e-4e1d856750c9. The library's generator verifies them
// (SPARK045, which names the expected value); this script is the writer. It must agree with
// SparkModelIds (libs/spark/Shared/Layering/SparkModelLayers.cs); the generator is the check.
//
// The alias is read from the folder's csproj (<SparkLibraryAlias>), the files from
// <SparkAppDataDir, default App_Data>/Model/*.json. An attribute's "group"/"tab" reference that named
// a re-stamped id is rewritten with it. Key order is kept; a missing id is inserted first.

import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const NAMESPACE = '036a66ee-1790-46de-9f0e-4e1d856750c9';

export function uuidV5(name, namespace = NAMESPACE) {
  const ns = Buffer.from(namespace.replace(/-/g, ''), 'hex');
  const hash = createHash('sha1').update(Buffer.concat([ns, Buffer.from(name, 'utf8')])).digest();
  const bytes = hash.subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** The object with `id` set to `value`: in place when present, else as the first key. */
function withId(element, value) {
  if ('id' in element) {
    element.id = value;
    return element;
  }
  return { id: value, ...element };
}

/** Stamps every id of one parsed model file; returns the new root and the old → new id map. */
export function stamp(alias, root) {
  const renamed = new Map();
  const po = root?.persistentObject;
  if (!po || typeof po.name !== 'string' || po.name.length === 0) {
    throw new Error('the file states no persistentObject.name');
  }
  const type = po.name;

  const stampArray = (items, seed) => (items ?? []).map((element) => {
    if (!element || typeof element !== 'object' || typeof element.name !== 'string') return element;
    const id = uuidV5(`${seed}.${element.name}`);
    if (element.id && element.id !== id) renamed.set(element.id, id);
    return withId(element, id);
  });

  let next = withId(po, uuidV5(`${alias}:${type}`));
  for (const collection of ['attributes', 'tabs', 'groups']) {
    if (Array.isArray(next[collection])) next[collection] = stampArray(next[collection], `${alias}:${type}.${collection}`);
  }
  for (const attribute of next.attributes ?? []) {
    for (const reference of ['group', 'tab']) {
      if (attribute && renamed.has(attribute[reference])) attribute[reference] = renamed.get(attribute[reference]);
    }
  }

  const result = { ...root, persistentObject: next };
  if (Array.isArray(root.queries)) result.queries = stampArray(root.queries, `${alias}:${type}.queries`);
  return result;
}

function aliasOf(folder) {
  const csproj = readdirSync(folder).find((f) => f.endsWith('.csproj'));
  if (!csproj) throw new Error(`${folder} holds no .csproj`);
  const text = readFileSync(join(folder, csproj), 'utf8');
  const alias = /<SparkLibraryAlias>\s*([^<\s]+)\s*<\/SparkLibraryAlias>/.exec(text)?.[1];
  if (!alias) throw new Error(`${csproj} sets no <SparkLibraryAlias>`);
  const appData = /<SparkAppDataDir>\s*([^<]+?)\s*<\/SparkAppDataDir>/.exec(text)?.[1] ?? 'App_Data';
  return { alias, modelDirectory: join(folder, appData, 'Model') };
}

function main(folders) {
  if (folders.length === 0) {
    console.error('Usage: npm run stamp:library-model-ids -- <library project folder> [...]');
    return 2;
  }
  for (const folder of folders.map((f) => resolve(f))) {
    const { alias, modelDirectory } = aliasOf(folder);
    if (!existsSync(modelDirectory)) {
      console.log(`${modelDirectory}: no model files.`);
      continue;
    }
    for (const file of readdirSync(modelDirectory).filter((f) => f.endsWith('.json')).sort()) {
      const path = join(modelDirectory, file);
      const text = readFileSync(path, 'utf8');
      const newline = text.includes('\r\n') ? '\r\n' : '\n';
      const updated = JSON.stringify(stamp(alias, JSON.parse(text)), null, 2).replace(/\n/g, newline)
        + (/\r?\n$/.test(text) ? newline : '');
      if (updated === text) {
        console.log(`${path}: ids are current.`);
        continue;
      }
      writeFileSync(path, updated);
      console.log(`${path}: ids written (alias '${alias}').`);
    }
  }
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exit(main(process.argv.slice(2)));
}
