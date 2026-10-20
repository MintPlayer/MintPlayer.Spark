// Translation-key guard (commerce X20 items 5 and 9, decision G1): every translation key the code asks
// for exists in a translations.json layer, and no lookup key outlives the enum member it labels.
//
// Three checks:
//   1. client keys: every literal key the ng-spark library or an app's ClientApp passes to the
//      translation service (`lang.t('common.save', …)`, `'auth.logout' | t`, and a component's own
//      prefixed helper `t('title')` → `notifications.spa.title`) is defined. ng-spark keys must be in a
//      library layer (libs/**/App_Data/translations.json), an app's keys in a library layer or its own.
//   2. lookup keys: every member of a `[SparkLookup]` enum has its label `lookup.{Enum}.{Member}` in the
//      declaring project's layer (for an app: any layer of that app, since its lookups may live in an
//      entity library next to the host).
//   3. unused lookup keys: every `lookup.*` key in any layer labels a member of some `[SparkLookup]` enum,
//      or is named by a `TranslatedString.FromKey("lookup.…")` literal.
// A key built at runtime ('x.' + name, template literals) cannot be checked statically and is skipped;
// other namespaces (model.*, actions.*, programUnits.*) are referenced by convention from App_Data, so
// "unused" is only decidable for lookup keys.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
import { strict as assert } from 'node:assert';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SKIP = new Set(['node_modules', 'bin', 'obj', 'dist', '.angular', '.nx', 'out-tsc', 'coverage', 'fixtures', 'TestResults']);

function* walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (SKIP.has(entry.name) || entry.name.startsWith('.')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full);
    else yield full;
  }
}

const rel = (file) => path.relative(REPO, file).split(path.sep).join('/');

/**
 * The keys a translations.json defines: the dotted paths to its leaves. A leaf holds only texts
 * (language → string); a namespace only objects (or null, which removes a library's namespace).
 * `$schema` and `_`-prefixed members are not keys.
 */
export function flattenTranslations(json, prefix = '', keys = new Set()) {
  for (const [name, value] of Object.entries(json)) {
    if (name === '$schema' || name.startsWith('_') || value === null || typeof value !== 'object') continue;
    const key = prefix ? `${prefix}.${name}` : name;
    const members = Object.entries(value).filter(([n]) => !n.startsWith('_'));
    if (members.length > 0 && members.every(([, v]) => typeof v === 'string')) keys.add(key);
    else flattenTranslations(value, key, keys);
  }
  return keys;
}

/** Comments removed, so documentation examples are not read as uses or declarations. */
export function stripComments(text) {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/.*$/gm, '$1');
}

/**
 * The literal keys a TypeScript or HTML source asks the translation service for. A component that
 * declares its own `t(key: string)` returning `….t('prefix.' + key, …)` prefixes its bare `t('x')`
 * and `this.t('x')` calls; any other `.t('x')` is a full key. Keys built at runtime are skipped.
 */
export function clientKeyUses(source) {
  const text = stripComments(source);
  const helper = /\bt\(key: string\)[^{]*\{\s*return\s+[^;]*?\.t\(\s*['"]([A-Za-z0-9_.-]+\.)['"]\s*\+\s*key\b/.exec(text);
  const prefix = helper?.[1] ?? '';
  const keys = new Set();
  for (const m of text.matchAll(/(this\.|\.|\b)t\(\s*['"]([A-Za-z0-9_.-]+)['"]/g)) {
    const [, receiver, key] = m;
    if (key.endsWith('.')) continue;
    const viaHelper = prefix && receiver !== '.';
    if (receiver === '' && m.index > 0 && /[A-Za-z0-9_$]/.test(text[m.index - 1])) continue;
    keys.add(viaHelper ? prefix + key : key);
  }
  for (const m of text.matchAll(/['"]([A-Za-z0-9_.-]+)['"]\s*\|\s*t\b/g)) {
    if (!m[1].endsWith('.')) keys.add(m[1]);
  }
  return keys;
}

/** The `[SparkLookup]` enums a C# source declares: name and member names, in order. */
export function lookupEnums(source) {
  const text = stripComments(source);
  const enums = [];
  const rx = /\[[^\]]*\bSparkLookup\b(?:\([^)]*\))?[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal)\s+)?enum\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z0-9_.]+\s*)?\{([^}]*)\}/g;
  for (const m of text.matchAll(rx)) {
    const members = m[2]
      .replace(/\[[^\]]*\]/g, '')
      .split(',')
      .map((part) => part.split('=')[0].trim().replace(/^@/, ''))
      .filter((name) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(name));
    enums.push({ name: m[1], members });
  }
  return enums;
}

/** The `lookup.*` keys a C# source names literally (a hand-written subset lookup reusing the enum's labels). */
export function literalLookupKeys(source) {
  return new Set([...stripComments(source).matchAll(/FromKey\(\s*"(lookup\.[A-Za-z0-9_.]+)"\s*\)/g)].map((m) => m[1]));
}

/** Missing client keys: those not defined in any of `layers` (sets of keys). */
export function missingKeys(used, layers) {
  return [...used].filter((key) => !layers.some((layer) => layer.has(key))).sort();
}

/** `lookup.*` keys of `layer` that label no declared member and no literal use. */
export function unusedLookupKeys(layer, declared, literal) {
  return [...layer].filter((key) => key.startsWith('lookup.') && !declared.has(key) && !literal.has(key)).sort();
}

// ---------------------------------------------------------------------------------------------
// The repository's inventory.

const layers = new Map();
for (const root of ['libs', 'apps']) {
  for (const file of walk(path.join(REPO, root))) {
    if (path.basename(file) === 'translations.json' && path.basename(path.dirname(file)) === 'App_Data') {
      layers.set(rel(file), flattenTranslations(JSON.parse(readFileSync(file, 'utf8'))));
    }
  }
}
const libraryLayers = [...layers].filter(([file]) => file.startsWith('libs/')).map(([, keys]) => keys);
const appOf = (file) => (file.startsWith('apps/') ? file.split('/')[1] : null);
const appLayers = (app) => [...layers].filter(([file]) => appOf(file) === app).map(([, keys]) => keys);

/** The directory of the nearest *.csproj above `file`. */
function projectDir(file) {
  let dir = path.dirname(file);
  while (dir.startsWith(REPO) && dir !== REPO) {
    if (readdirSync(dir).some((name) => name.endsWith('.csproj'))) return dir;
    dir = path.dirname(dir);
  }
  return null;
}

const declaredLookups = []; // { file, name, members }
const literalKeys = new Set();
for (const root of ['libs', 'apps']) {
  for (const file of walk(path.join(REPO, root))) {
    if (!file.endsWith('.cs')) continue;
    const source = readFileSync(file, 'utf8');
    if (!source.includes('SparkLookup') && !source.includes('"lookup.')) continue;
    for (const declared of lookupEnums(source)) declaredLookups.push({ file: rel(file), ...declared });
    for (const key of literalLookupKeys(source)) literalKeys.add(key);
  }
}
const declaredKeys = new Set(declaredLookups.flatMap((l) => l.members.map((m) => `lookup.${l.name}.${m}`)));

test('reads the translation layers of core and the apps', () => {
  assert.ok(layers.has('libs/spark/MintPlayer.Spark/App_Data/translations.json'), 'the core layer was not found');
  assert.ok([...layers.keys()].some((file) => file.startsWith('apps/CodeCoverage/')), 'no CodeCoverage layer was found');
});

test('ng-spark asks only for keys a library layer defines', () => {
  const missing = [];
  for (const file of walk(path.join(REPO, 'libs', 'node_packages', 'ng-spark'))) {
    if (!/\.(ts|html)$/.test(file) || /\.spec\.ts$/.test(file)) continue;
    for (const key of missingKeys(clientKeyUses(readFileSync(file, 'utf8')), libraryLayers)) missing.push(`${key} (${rel(file)})`);
  }
  assert.deepEqual(missing, [], 'add these keys to the library translations.json that owns the entry point');
});

test("each app's ClientApp asks only for keys its own or a library layer defines", () => {
  const missing = [];
  for (const file of walk(path.join(REPO, 'apps'))) {
    if (!/\.(ts|html)$/.test(file) || /\.spec\.ts$/.test(file) || !rel(file).includes('/ClientApp/')) continue;
    const app = appOf(rel(file));
    for (const key of missingKeys(clientKeyUses(readFileSync(file, 'utf8')), [...appLayers(app), ...libraryLayers])) missing.push(`${key} (${rel(file)})`);
  }
  assert.deepEqual(missing, [], "add these keys to the app's App_Data/translations.json");
});

test('every [SparkLookup] member has its label in the declaring project', () => {
  const missing = [];
  for (const lookup of declaredLookups) {
    const app = appOf(lookup.file);
    const dir = projectDir(path.join(REPO, lookup.file));
    const own = dir ? layers.get(rel(path.join(dir, 'App_Data', 'translations.json'))) : undefined;
    const candidates = app ? appLayers(app) : own ? [own] : [];
    for (const member of lookup.members) {
      const key = `lookup.${lookup.name}.${member}`;
      if (!candidates.some((layer) => layer.has(key))) missing.push(`${key} (${lookup.file})`);
    }
  }
  assert.deepEqual(missing, [], 'add these labels to the translations.json of the project that declares the enum');
});

test('no lookup.* key outlives its enum member', () => {
  const unused = [];
  for (const [file, keys] of layers) {
    for (const key of unusedLookupKeys(keys, declaredKeys, literalKeys)) unused.push(`${key} (${file})`);
  }
  assert.deepEqual(unused, [], 'remove these keys, or restore the [SparkLookup] member they label');
});

// ---------------------------------------------------------------------------------------------
// The guard proves it fires (memory: a clean grep is not evidence).

test('fixture: a missing client key is reported, a defined one is not', () => {
  const layer = flattenTranslations({ common: { save: { en: 'Save' } }, $schema: 'x', _note: 'comment' });
  const uses = clientKeyUses(`
    // lang.t('in.a.comment') is documentation, not a use
    const a = this.lang.t('common.save', 'Save');
    const b = this.lang.t('common.unexpectedError', 'An unexpected error occurred');
    const c = this.lang.t('dynamic.' + name);
    template: \`<span>{{ 'auth.logout' | t }}</span>\``);
  assert.deepEqual([...uses].sort(), ['auth.logout', 'common.save', 'common.unexpectedError']);
  assert.deepEqual(missingKeys(uses, [layer]), ['auth.logout', 'common.unexpectedError']);
});

test('fixture: a component helper prefixes its bare t() calls', () => {
  const uses = clientKeyUses(`
    template: \`<span>{{ t('title') }}</span>\`
    protected t(key: string): string {
      return this.language.t('notifications.spa.' + key, TEXTS[key]);
    }
    private other = this.t('empty');
    private full = this.language.t('common.close', 'Close');`);
  assert.deepEqual([...uses].sort(), ['common.close', 'notifications.spa.empty', 'notifications.spa.title']);
});

test('fixture: a [SparkLookup] enum yields its members; comments and other enums do not', () => {
  const enums = lookupEnums(`
    /// <example>[SparkLookup] public enum EDocumented { Nope }</example>
    [SparkLookup(StringKeys = true, DisplayType = ELookupDisplayType.Modal)]
    public enum EProjectBasis
    {
        /// <summary>Like-for-like.</summary>
        [SparkLookupKey("scoped")] Scoped,
        Projection = 4,
    }
    public enum ENotALookup { A, B }`);
  assert.deepEqual(enums, [{ name: 'EProjectBasis', members: ['Scoped', 'Projection'] }]);
});

test('fixture: an unused lookup key is reported, a declared or literal one is not', () => {
  const layer = flattenTranslations({ lookup: { ECar: { Sold: { en: 'Sold' }, Gone: { en: 'Gone' } }, ERole: { Admin: { en: 'Admin' } } } });
  const declared = new Set(['lookup.ECar.Sold']);
  const literal = literalLookupKeys('Values = TranslatedString.FromKey("lookup.ERole.Admin")');
  assert.deepEqual(unusedLookupKeys(layer, declared, literal), ['lookup.ECar.Gone']);
});
