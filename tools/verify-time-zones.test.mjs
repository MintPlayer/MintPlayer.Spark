// Time-zone guard (commerce PRD K10, X16): the client names zones and formats instants in ONE place.
//
// Every date a viewer sees goes through ng-spark's date-time entry point (`<spark-date-time>`, the
// `sparkDateTime` pipe, `formatSparkDateTime`). A private `Intl.DateTimeFormat`, a `timeZone:` option or a
// `DatePipe` zone argument elsewhere is a second implementation that drifts: the identity provider's own
// date pipe and three CodeCoverage formatters were exactly that. And Angular's `DatePipe` IGNORES an IANA id
// as its zone argument and silently falls back (docs/guide-dates-and-sorting.md), so a zone argument there
// is a bug even when it looks right.
//
// The server half is BannedSymbols.TimeZones.txt (Directory.Build.targets). This file is the client half.
// The fixtures under tools/fixtures/time-zone-guard prove each rule fires: a clean scan is not evidence.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
import { strict as assert } from 'node:assert';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SKIP = new Set(['node_modules', 'bin', 'obj', 'dist', '.angular', '.nx', 'out-tsc', 'coverage', 'wwwroot']);
const FIXTURES = path.join(REPO, 'tools', 'fixtures', 'time-zone-guard');

/** The one directory allowed to format instants and name zones. */
export const ALLOWED = ['libs/node_packages/ng-spark/date-time/'];

/** Removes comments, so a sentence ABOUT `Intl.DateTimeFormat` is not a use of it. Strings are left alone. */
export function stripComments(source, kind) {
  const blank = m => m.replace(/[^\n]/g, ' ');
  // HTML comments in both kinds: a .ts component carries its template inline.
  const withoutHtml = source.replace(/<!--[\s\S]*?-->/g, blank);
  if (kind === 'html') return withoutHtml;
  return withoutHtml
    .replace(/\/\*[\s\S]*?\*\//g, blank)
    .replace(/(^|[^:'"`])\/\/[^\n]*/g, (m, lead) => lead + ' '.repeat(m.length - lead.length));
}

/** Splits `text` on `separator` outside quotes and brackets. */
function splitTopLevel(text, separator) {
  const parts = [];
  let depth = 0, quote = null, start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === quote && text[i - 1] !== '\\') quote = null; continue; }
    if (c === '\'' || c === '"' || c === '`') quote = c;
    else if (c === '(' || c === '[' || c === '{') depth++;
    else if (c === ')' || c === ']' || c === '}') depth--;
    else if (c === separator && depth === 0) { parts.push(text.slice(start, i)); start = i + 1; }
  }
  parts.push(text.slice(start));
  return parts;
}

/** The text of a call's arguments from the `(` at `open`, or null when unbalanced. */
function callArguments(text, open) {
  let depth = 0, quote = null;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === quote && text[i - 1] !== '\\') quote = null; continue; }
    if (c === '\'' || c === '"' || c === '`') quote = c;
    else if (c === '(') depth++;
    else if (c === ')' && --depth === 0) return text.slice(open + 1, i);
  }
  return null;
}

/**
 * The arguments of a `| date:…` pipe from just after `date:`, up to the end of the pipe expression. Only
 * single quotes open a string: a double quote is the end of the attribute the expression sits in.
 */
function datePipeArguments(text, from) {
  let depth = 0, quote = false, i = from;
  for (; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === '\'') quote = false; continue; }
    if (c === '\'') { quote = true; continue; }
    if (c === '(') depth++;
    else if (c === ')') { if (depth === 0) break; depth--; }
    else if (c === '"' || c === '`') break;
    else if (depth === 0 && (c === '|' || c === ';' || c === '\n' || (c === '}' && text[i + 1] === '}'))) break;
  }
  return text.slice(from, i);
}

const lineOf = (text, index) => text.slice(0, index).split('\n').length;

/**
 * Every violation in one file's text: `{ line, rule }`. `kind` is `ts` or `html`; a `.ts` file is also scanned
 * for template syntax, because components carry inline templates.
 */
export function findViolations(source, kind) {
  const text = stripComments(source, kind);
  const found = [];
  const add = (index, rule) => found.push({ line: lineOf(text, index), rule });

  for (const m of text.matchAll(/\bIntl\s*\.\s*DateTimeFormat\b/g)) add(m.index, 'Intl.DateTimeFormat');
  for (const m of text.matchAll(/\btimeZone\s*:/g)) add(m.index, 'timeZone: option');
  for (const m of text.matchAll(/\bDATE_PIPE_DEFAULT_(?:OPTIONS|TIMEZONE)\b/g)) add(m.index, 'DatePipe default zone');

  // `| date:'short':'+0200'`: a second argument is the zone.
  for (const m of text.matchAll(/\|\s*date\s*:/g)) {
    const args = splitTopLevel(datePipeArguments(text, m.index + m[0].length), ':');
    if (args.length >= 2 && args[1].trim() !== '') add(m.index, 'DatePipe zone argument');
  }
  // formatDate(value, format, locale, timezone) and datePipe.transform(value, format, timezone).
  for (const m of text.matchAll(/\bformatDate\s*\(/g)) {
    const args = callArguments(text, m.index + m[0].length - 1);
    if (args !== null && splitTopLevel(args, ',').length >= 4) add(m.index, 'formatDate zone argument');
  }
  for (const m of text.matchAll(/\b[\w$]*[Dd]ate[Pp]ipe\s*\.\s*transform\s*\(/g)) {
    const args = callArguments(text, m.index + m[0].length - 1);
    if (args !== null && splitTopLevel(args, ',').length >= 3) add(m.index, 'DatePipe zone argument');
  }
  return found;
}

/** The client sources the guard covers: ng-spark and every app's ClientApp/src. Specs may mock `Intl`. */
export function clientSources() {
  const files = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (SKIP.has(entry.name)) continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/\.(ts|html)$/.test(entry.name) && !/\.spec\.ts$/.test(entry.name) && !/\.d\.ts$/.test(entry.name)) files.push(full);
    }
  };
  walk(path.join(REPO, 'libs', 'node_packages'));
  const apps = path.join(REPO, 'apps');
  for (const app of readdirSync(apps, { withFileTypes: true })) {
    if (!app.isDirectory()) continue;
    const appDir = path.join(apps, app.name);
    for (const project of readdirSync(appDir, { withFileTypes: true })) {
      const src = path.join(appDir, project.name, 'ClientApp', 'src');
      if (project.isDirectory() && existsSync(src)) walk(src);
    }
  }
  return files;
}

const rel = (file) => path.relative(REPO, file).split(path.sep).join('/');

test('the violating fixtures trip every rule (the guard is proven to fire)', () => {
  const ts = findViolations(readFileSync(path.join(FIXTURES, 'violations.ts.fixture'), 'utf8'), 'ts').map(v => v.rule);
  const html = findViolations(readFileSync(path.join(FIXTURES, 'violations.html.fixture'), 'utf8'), 'html').map(v => v.rule);
  for (const rule of ['Intl.DateTimeFormat', 'timeZone: option', 'DatePipe default zone', 'formatDate zone argument', 'DatePipe zone argument']) {
    assert.ok(ts.includes(rule), `violations.ts.fixture did not trip "${rule}" (got: ${ts.join(', ')})`);
  }
  assert.ok(html.filter(r => r === 'DatePipe zone argument').length >= 2, `violations.html.fixture: ${html.join(', ')}`);
});

test('the clean fixture passes (comments, viewer-zone dates and format strings with colons are fine)', () => {
  const clean = findViolations(readFileSync(path.join(FIXTURES, 'clean.ts.fixture'), 'utf8'), 'ts');
  assert.deepEqual(clean, []);
});

test('finds the client sources (ng-spark and the apps)', () => {
  const files = clientSources().map(rel);
  assert.ok(files.some(f => f.startsWith('libs/node_packages/ng-spark/grid/')), 'ng-spark grid not scanned');
  assert.ok(files.some(f => f.startsWith('apps/CodeCoverage/CodeCoverage/ClientApp/src/')), 'CodeCoverage ClientApp not scanned');
});

test('no client code outside the date-time entry point formats instants or names zones (K10)', () => {
  const problems = [];
  for (const file of clientSources()) {
    const name = rel(file);
    if (ALLOWED.some(prefix => name.startsWith(prefix))) continue;
    for (const v of findViolations(readFileSync(file, 'utf8'), file.endsWith('.html') ? 'html' : 'ts')) {
      problems.push(`${name}:${v.line} ${v.rule}`);
    }
  }
  assert.deepEqual(problems, [],
    'Format dates with <spark-date-time>, the sparkDateTime pipe or formatSparkDateTime from @mintplayer/ng-spark/date-time ' +
    '(docs/guide-dates-and-sorting.md, "One facility"):\n  ' + problems.join('\n  '));
});
