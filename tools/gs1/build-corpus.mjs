#!/usr/bin/env node
// Builds libs/gs1/corpus/gs1-corpus.json, the GS1 parsing corpus (warehouse W12): scan data, element strings,
// bracketed AI data and GS1 Digital Link URIs, each with the result GS1's own Barcode Syntax Engine gives for it.
// MintPlayer.Spark.Gs1.Tests (C#) and ng-spark's scanning/src/gs1-corpus.spec.ts (TypeScript) run our parsers
// (libs/gs1/MintPlayer.Spark.Gs1/pg) over it and require the same verdict and the same (AI, value) list.
//
// Usage: node tools/gs1/build-corpus.mjs <gs1encoder.mjs> <engine C sources> [out.json]
//   <gs1encoder.mjs>     the engine's WebAssembly build (GS1encoder JS wrapper; a gs1encoder release or
//                        `make -C src/c-lib wasm` in https://github.com/gs1/gs1-syntax-engine)
//   <engine C sources>   the matching src/c-lib folder (ai.c, dl.c, scandata.c): the INPUTS of their unit-test
//                        vectors join the corpus; their expected results are not used (those tests run with
//                        options of their own)
//
// Cases come from three places: those test vectors; one valid element string for every AI of
// tools/gs1/gs1-syntax-dictionary.txt (in two input forms, plus a Digital Link form where the AI may appear in one, and
// a broken variant); and the hand-written cases below (FNC1/GS handling, symbology identifiers, day-00 dates, Digital
// Link prefixes, qualifier order, query attributes, percent-encoding). Every expectation is the engine's: it runs with
// the RequisiteAIs validation off, because AI associations are judged over all carriers on an item, not per barcode,
// and every other option at its default.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const [enginePath, sourcesDir, outArg] = process.argv.slice(2);
if (!enginePath || !sourcesDir) {
  console.error('usage: node tools/gs1/build-corpus.mjs <gs1encoder.mjs> <engine C sources (ai.c, dl.c, scandata.c)> [out.json]');
  process.exit(2);
}
const outPath = path.resolve(outArg ?? path.join(here, '../../libs/gs1/corpus/gs1-corpus.json'));
const dictionaryPath = path.join(here, 'gs1-syntax-dictionary.txt');

const { GS1encoder } = await import(pathToFileURL(path.resolve(enginePath)).href);
const engine = await GS1encoder.create();
engine.setValidationEnabled(GS1encoder.validation.RequisiteAIs, false);

const GS = '\x1D';
// Inputs whose AI data and HRI text from the engine do not list the same elements (a check on parseAiDataStr).
const hriDisagreements = [];

// ---------------------------------------------------------------------------------------------------------------
// The engine as the oracle
// ---------------------------------------------------------------------------------------------------------------

// The engine's bracketed AI string as (AI, value) pairs: `\(` is a literal bracket, `|` separates composite parts.
function parseAiDataStr(s) {
  const elements = [];
  let i = 0;
  while (i < s.length) {
    if (s[i] === '|') { i++; continue; }
    if (s[i] !== '(') throw new Error(`unexpected AI data from the engine: ${JSON.stringify(s)}`);
    const close = s.indexOf(')', i);
    const ai = s.slice(i + 1, close);
    let value = '';
    let j = close + 1;
    while (j < s.length && s[j] !== '(' && s[j] !== '|') {
      if (s[j] === '\\' && s[j + 1] === '(') { value += '('; j += 2; } else { value += s[j]; j += 1; }
    }
    elements.push([ai, value]);
    i = j;
  }
  return elements;
}

function resetEngine() {
  try { engine.sym = GS1encoder.symbology.NONE; } catch { /* keeps the previous symbology */ }
  try { engine.dataStr = ''; } catch { /* nothing to clear */ }
}

// Runs one case through the engine. `elementString` cases are AI data with GS (FNC1) separators and an optional leading
// GS; the engine reads them as `^`-prefixed raw data. EAN-13/EAN-8 scans carry a GTIN that the engine keeps as the
// primary message rather than as AI (01): the corpus lists it as (01), zero-padded to 14 digits, before any composite.
function evaluate(kind, input) {
  resetEngine();
  try {
    if (kind === 'bracketed') engine.aiDataStr = input;
    else if (kind === 'elementString') engine.dataStr = '^' + (input.startsWith(GS) ? input.slice(1) : input).replaceAll(GS, '^');
    else if (kind === 'digitalLink') engine.dataStr = input;
    else if (kind === 'scan') engine.scanData = input;
    else throw new Error(`unknown kind ${kind}`);
  } catch (err) {
    if (!/^GS1encoder/.test(err?.constructor?.name ?? '')) throw err;
    return { valid: false, engineError: err.message };
  }
  let elements = parseAiDataStr(engine.aiDataStr ?? '');
  const hri = engine.hri ?? [];
  const sym = engine.sym;
  if (kind === 'scan' && (sym === GS1encoder.symbology.EAN13 || sym === GS1encoder.symbology.EAN8)) {
    const primary = engine.dataStr.split('|')[0];
    elements = [['01', primary.padStart(14, '0')], ...elements];
  } else if (hri.length !== elements.length || hri.some((line, i) => line !== `(${elements[i][0]}) ${elements[i][1]}`)) {
    hriDisagreements.push(`${JSON.stringify(input)}: ${JSON.stringify(elements)} vs HRI ${JSON.stringify(hri)}`);
  }
  if (elements.length === 0) return { valid: false, engineError: 'accepted as plain (non-GS1) data: no AIs' };
  if (zeroSuppressedQueryGtin(input)) {
    return { valid: false, engineError: 'AI (01) value is too short', note: ZERO_SUPPRESSED_NOTE };
  }
  return { valid: true, elements };
}

// One correction to the engine build: it pads a GTIN-8, -12 or -13 in a Digital Link (01) QUERY parameter to GTIN-14
// although permitZeroSuppressedGTINinDLuris is off, while it rejects the same GTIN in the path. The engine sources it
// is paired with pad in neither place unless that option is on (dl.c parseDLAIvaluePair; the test_parseDLuri vectors
// after "gated on the feature flag" expect AI_VALUE_IS_TOO_SHORT), so such cases record the sources' verdict.
const ZERO_SUPPRESSED_NOTE = 'engine build accepts this: it zero-pads (01) in query parameters regardless of '
  + 'permitZeroSuppressedGTINinDLuris; its sources reject it (see tools/gs1/build-corpus.mjs)';
function zeroSuppressedQueryGtin(input) {
  const q = input.indexOf('?');
  if (!/^(\]Q1|\]d1|\]J0)?https?:\/\//i.test(input) || q < 0) return false;
  return input.slice(q + 1).split('#')[0].split('&').some((p) => /^01=(\d{8}|\d{12}|\d{13})$/.test(p));
}

// ---------------------------------------------------------------------------------------------------------------
// Inputs of the engine's unit-test vectors
// ---------------------------------------------------------------------------------------------------------------

// Reads a C string literal starting at text[pos] (the opening quote).
function readCString(text, pos) {
  let value = '';
  let i = pos + 1;
  while (text[i] !== '"') {
    const c = text[i];
    if (c !== '\\') { value += c; i += 1; continue; }
    const e = text[i + 1];
    if (e === 'x') {
      let j = i + 2;
      while (/[0-9a-fA-F]/.test(text[j])) j += 1;
      value += String.fromCharCode(parseInt(text.slice(i + 2, j), 16));
      i = j;
    } else if (/[0-7]/.test(e)) {
      let j = i + 1;
      while (j < i + 4 && /[0-7]/.test(text[j])) j += 1;
      value += String.fromCharCode(parseInt(text.slice(i + 1, j), 8));
      i = j;
    } else {
      const simple = { n: '\n', t: '\t', r: '\r', a: '\x07', b: '\b', f: '\f', v: '\v', '\\': '\\', '"': '"', "'": "'", '?': '?' };
      if (!(e in simple)) throw new Error(`unknown C escape \\${e}`);
      value += simple[e];
      i += 2;
    }
  }
  return { value, end: i + 1 };
}

// The arguments of the macro call whose `(` precedes text[pos]: a string-literal argument (adjacent literals joined)
// becomes a string, anything else null.
function readArgs(text, pos) {
  const args = [];
  let strs = [];
  let raw = '';
  let depth = 0;
  const finish = () => { args.push(strs.length && raw.trim() === '' ? strs.join('') : null); strs = []; raw = ''; };
  while (pos < text.length) {
    const c = text[pos];
    if (c === '"') { const s = readCString(text, pos); strs.push(s.value); pos = s.end; continue; }
    if (c === '/' && text[pos + 1] === '/') { pos = text.indexOf('\n', pos); continue; }
    if (c === '/' && text[pos + 1] === '*') { pos = text.indexOf('*/', pos) + 2; continue; }
    if (c === '(') depth += 1;
    if (c === ')') {
      if (depth === 0) { finish(); return { args, end: pos + 1 }; }
      depth -= 1;
    }
    if (c === ',' && depth === 0) { finish(); pos += 1; continue; }
    raw += c;
    pos += 1;
  }
  throw new Error('unterminated macro call');
}

// Which inputs each test macro carries, and as what. `raw` is the engine's `^`-prefixed data string.
const MACROS = {
  test_parseAIdata: (a) => [['bracketed', a[1]], ['raw', a[2]]],
  test_processAIdata: (a) => [['raw', a[1]]],
  test_validateAIs: (a) => [['bracketed', a[2]]],
  test_errMarkup: (a) => [['bracketed', a[0]]],
  test_parseDLuri: (a) => [['digitalLink', a[1]], ['raw', a[2]]],
  test_testGenerateDLuri: (a) => [['bracketed', a[2]], ['digitalLink', a[3]]],
  test_testProcessScanData: (a) => [['scan', a[1]]],
  test_testGenerateScanData: (a) => [['scan', a[3]]],
};

function vectorInputs() {
  const found = [];
  for (const file of ['ai.c', 'dl.c', 'scandata.c']) {
    const text = fs.readFileSync(path.join(sourcesDir, file), 'utf8').replace(/\r\n/g, '\n');
    const lineOf = (index) => text.slice(0, index).split('\n').length;
    const call = new RegExp(`\\b(${Object.keys(MACROS).join('|')})\\s*\\(`, 'g');
    let m;
    while ((m = call.exec(text))) {
      const lineStart = text.lastIndexOf('\n', m.index) + 1;
      const before = text.slice(lineStart, m.index);
      if (/^\s*#/.test(before) || before.includes('//') || /^\s*\*/.test(before)) continue;   // definitions, comments
      const { args, end } = readArgs(text, m.index + m[0].length);
      call.lastIndex = end;
      for (const [kind, input] of MACROS[m[1]](args)) {
        if (input !== null && input !== undefined) found.push({ kind, input, source: `${file}:${lineOf(m.index)} ${m[1]}` });
      }
    }
    // test_ai_linters triggers every linter error through a real AI: { "(AI)value", GS1_LINTER_... }
    const linterRow = /\{\s*("(?:[^"\\]|\\.)*")\s*,\s*GS1_LINTER_[A-Z0-9_]+\s*\}/g;
    while ((m = linterRow.exec(text))) {
      const lineStart = text.lastIndexOf('\n', m.index) + 1;
      if (text.slice(lineStart, m.index).includes('//')) continue;
      found.push({ kind: 'bracketed', input: readCString(m[1], 0).value, source: `${file}:${lineOf(m.index)} test_ai_linters` });
    }
  }
  return found;
}

// Normalises a vector input to the kind our API reads it as: an element string never starts with `(` or `]`, a scan
// always starts with `]`, and the engine's `^` FNC1 becomes GS. Composite (`|`) encoder syntax and non-AI raw data
// have no element-string equivalent and are left out.
function normalise({ kind, input, source }) {
  const out = [];
  if (kind === 'raw') {
    if (!input.startsWith('^') || input.includes('|') || input.includes('\\')) return out;
    const data = input.slice(1).replaceAll('^', GS);
    if (data.startsWith('(') || data.startsWith(']')) return out;
    out.push({ kind: 'elementString', input: data, source });
    out.push({ kind: 'scan', input: ']C1' + data, source: `${source} (as ]C1 scan data)` });
    return out;
  }
  if (kind === 'bracketed') {
    if (input.includes('|')) return out;
    if (input.startsWith('(')) out.push({ kind, input, source });
    else if (!input.startsWith(']') && !/^https?:/i.test(input)) out.push({ kind: 'elementString', input, source });
    return out;
  }
  if (kind === 'scan') {
    if (input.startsWith(']')) out.push({ kind, input, source });
    else if (input.startsWith('(')) out.push({ kind: 'bracketed', input, source });
    else if (/^https?:/i.test(input)) out.push({ kind: 'digitalLink', input, source });
    else out.push({ kind: 'elementString', input, source });
    return out;
  }
  out.push({ kind, input, source });
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// One valid element string for every AI of the dictionary
// ---------------------------------------------------------------------------------------------------------------

const componentPattern = /^(\[)?([NXYZ])(\d*)(\.\.(\d+))?(\])?((?:,[a-z0-9]+)*)(\])?$/;

function readDictionary() {
  const ais = [];
  for (const line of fs.readFileSync(dictionaryPath, 'utf8').split(/\r?\n/)) {
    if (!line.trim() || line.startsWith('#')) continue;
    const hash = line.indexOf('#');
    const tokens = (hash >= 0 ? line.slice(0, hash) : line).trim().split(/\s+/);
    const [first, last = first] = tokens.shift().split('-');
    const flags = tokens.length && !componentPattern.test(tokens[0]) ? tokens.shift() : '';
    const components = [];
    while (tokens.length && componentPattern.test(tokens[0])) {
      const [, open, charset, fixed, range, max, , linters] = componentPattern.exec(tokens.shift());
      const min = range ? (fixed ? Number(fixed) : 1) : Number(fixed);
      components.push({ charset, min, max: range ? Number(max) : min, optional: Boolean(open), linters: linters ? linters.slice(1).split(',') : [] });
    }
    const attributes = tokens.join(' ');
    for (let n = Number(first); n <= Number(last); n++) {
      ais.push({
        ai: String(n).padStart(first.length, '0'),
        predefined: flags.includes('*'),
        dlAttribute: flags.includes('?'),
        dlKey: /(^| )dlpkey(=| |$)/.test(attributes),
        components,
      });
    }
  }
  return ais;
}

function gs1CheckDigit(digits) {
  let sum = 0;
  for (let i = 0; i < digits.length; i++) {
    const d = Number(digits[digits.length - 1 - i]);
    sum += i % 2 === 0 ? d * 3 : d;
  }
  return String((10 - (sum % 10)) % 10);
}

const fill = (pattern, n) => pattern.repeat(Math.ceil(n / pattern.length)).slice(0, n);
const pickLength = (c, preferred) => Math.max(c.min, Math.min(c.max, preferred));

// A value for one component, satisfying its linters (`variant` picks an alternative where one exists).
function componentValue(c, variant) {
  const has = (l) => c.linters.includes(l);
  const n = (preferred) => pickLength(c, preferred);
  if (has('csum')) {
    const len = c.max;
    const body = fill((has('gcppos2') ? '0' : '') + '952123454321', len - 1);
    return body + gs1CheckDigit(body);
  }
  if (has('csumalpha')) return '1987654Ad4X4bL5ttr2310c2K'.slice(0, c.max);
  if (has('couponcode')) return '012345612345611110123';          // from the linter's own unit tests
  if (has('couponposoffer')) return '001234561234560123456';
  if ((has('gcppos1') || has('gcppos2')) && c.charset !== 'N') return fill((has('gcppos2') ? '0' : '') + '9521234ABC', n(10));
  if (has('yymmd0')) return variant % 2 === 1 ? '250100' : '250131';
  if (has('yymmdd')) return '240229';
  if (has('yyyymmdd')) return '20240229';
  if (has('hhmi')) return '2359';
  if (has('hh')) return '23';
  if (has('mi')) return '59';
  if (has('ss')) return '59';
  if (has('iso3166alpha2')) return 'BE';
  if (has('iso3166') || has('iso3166999')) return fill(variant % 2 === 1 && has('iso3166999') ? '999' : '056', c.min === c.max ? c.max : 3 * Math.max(1, Math.floor(n(3) / 3)));
  if (has('iso4217')) return '978';
  if (has('iso5218')) return '2';
  if (has('latitude')) return '0900000000';
  if (has('longitude')) return '1800000000';
  if (has('pieceoftotal')) return c.max === 2 ? '12' : fill('0', c.max / 2 - 1) + '1' + fill('0', c.max / 2 - 1) + '2';
  if (has('yesno')) return '1';
  if (has('winding')) return '9';
  if (has('zero')) return '0';
  if (has('hyphen')) return '-';
  if (has('nonzero')) return fill('1203', n(4));
  if (has('nozeroprefix')) return fill('123', n(3));
  if (has('importeridx')) return 'A';
  if (has('iban')) return 'GB82WEST12345698765432';
  if (has('mediatype')) return '01';
  if (has('packagetype')) return 'BX';
  if (has('posinseqslash')) return '1/2';
  if (has('pcenc')) return variant % 2 === 1 ? fill('A%20B', n(5)) : fill('ABC', n(6));
  if (c.charset === 'N') return fill('1234567890', n(6));
  if (c.charset === 'Y') return fill('AB-12/#', n(6));
  if (c.charset === 'Z') return fill('AbCd-_09', Math.max(c.min, Math.min(c.max, 8)));
  return fill(variant % 2 === 1 ? 'Ab1-./' : 'ABC123', n(6));
}

// Candidate values for an AI: synthesised from its components, then every value the engine's own vectors use for it.
function candidateValues(entry, vectorValues) {
  const candidates = [];
  for (const variant of [0, 1]) {
    candidates.push(entry.components.map((c) => componentValue(c, variant)).join(''));
    candidates.push(entry.components.filter((c) => !c.optional).map((c) => componentValue(c, variant)).join(''));
  }
  candidates.push(...(vectorValues.get(entry.ai) ?? []));
  return [...new Set(candidates)];
}

// URI-escapes a value as the engine's DL generator does: unreserved characters stay, the rest becomes %XX.
const uriEscape = (s) => [...s].map((ch) => (/[A-Za-z0-9\-._~]/.test(ch) ? ch : '%' + ch.charCodeAt(0).toString(16).toUpperCase().padStart(2, '0'))).join('');

// ---------------------------------------------------------------------------------------------------------------
// Hand-written cases
// ---------------------------------------------------------------------------------------------------------------

const withCheckDigit = (body) => body + gs1CheckDigit(body);
const GTIN = withCheckDigit('0952123454321');
const SSCC = withCheckDigit('09521234567890123');
const GDTI = withCheckDigit('952123454321');                     // also a GLN, GCN or GRAI body: 13 digits
const DL = 'https://id.gs1.org';

const special = [
  // FNC1 / GS handling in element strings and scan data
  ['elementString', `${GS}01${GTIN}10ABC123${GS}17250131`, 'leading GS (FNC1 in first position)'],
  ['elementString', `01${GTIN}10ABC123${GS}17250131`, 'no leading GS'],
  ['elementString', `01${GTIN}${GS}10ABC123`, 'superfluous GS after a predefined-length AI'],
  ['elementString', `01${GTIN}10ABC123${GS}`, 'trailing GS'],
  ['elementString', `01${GTIN}10ABC123${GS}${GS}17250131`, 'double GS'],
  ['elementString', `${GS}${GS}01${GTIN}`, 'two leading GS'],
  ['elementString', `10ABC12317250131`, 'missing GS: (10) swallows the next AI'],
  ['elementString', `10ABC123${GS}21SERIAL${GS}17250131`, 'GS after each variable-length value'],
  ['elementString', `0${GTIN}`, 'unknown AI prefix'],
  ['elementString', `01${GTIN.slice(0, 13)}`, 'predefined length cut short'],
  ['elementString', `01${GTIN.slice(0, 13)}${GS}10ABC`, 'predefined length cut short by a GS'],
  ['elementString', `${GS}`, 'only a GS'],
  ['elementString', '', 'empty'],
  ['elementString', `3103000123${GS}3922012345`, 'decimal AIs'],
  ['elementString', `80030${GDTI}1ABC${GS}10X`, 'GRAI with serial'],
  ['scan', `]C101${GTIN}10ABC123${GS}17250131`, 'GS1-128'],
  ['scan', `]C1${GS}01${GTIN}`, 'GS straight after ]C1'],
  ['scan', `]C101${GTIN}${GS}`, 'trailing GS after ]C1 data'],
  ['scan', `]C1`, 'symbology identifier only'],
  ['scan', `]C0ABC`, 'Code 128 without FNC1'],
  ['scan', `]e001${GTIN}3103000123`, 'GS1 DataBar'],
  ['scan', `]e001${GTIN}10LOT${GS}21S1`, 'GS1 DataBar Expanded'],
  ['scan', `]d201${GTIN}17250131${GS}`, 'GS1 DataMatrix with trailing GS'],
  ['scan', `]d2${GS}01${GTIN}`, 'GS straight after ]d2'],
  ['scan', `]d201${GTIN}10A^B`, 'caret in scan data'],
  ['scan', `]Q301${GTIN}21SER${GS}10LOT`, 'GS1 QR Code'],
  ['scan', `]Q3`, 'GS1 QR Code without data'],
  ['scan', `]J101${GTIN}10DOT`, 'GS1 DotCode'],
  ['scan', `]J0TEXT`, 'DotCode plain data'],
  ['scan', `]E0${GTIN.slice(1)}`, 'EAN-13'],
  ['scan', `]E04006381333931`, 'EAN-13 (2)'],
  ['scan', `]E0${GTIN.slice(1, 13)}0`, 'EAN-13 with a wrong check digit'],
  ['scan', `]E0${GTIN.slice(2)}`, 'EAN-13 too short (12 digits)'],
  ['scan', `]E0${GTIN}`, 'EAN-13 too long (14 digits)'],
  ['scan', `]E0952123454321A`, 'EAN-13 with a letter'],
  ['scan', `]E0${GTIN.slice(1)}|]e010LOT${GS}21S1`, 'EAN-13 with a composite component'],
  ['scan', `]E0${GTIN.slice(1)}|]e0`, 'EAN-13 with an empty composite component'],
  ['scan', `]E0${GTIN.slice(1)}|]C110LOT`, 'EAN-13 with a composite after the wrong identifier'],
  ['scan', `]E496385074`, 'EAN-8'],
  ['scan', `]E496385075`, 'EAN-8 with a wrong check digit'],
  ['scan', `]E496385074|]e010LOT`, 'EAN-8 with a composite component'],
  ['scan', `]E3${GTIN.slice(1)}`, 'EAN with add-on identifier'],
  ['scan', `]E1${GTIN.slice(1)}`, 'add-on only identifier'],
  ['scan', `]Q1${DL}/01/${GTIN}/10/LOT?17=250131`, 'Digital Link in a QR Code'],
  ['scan', `]d1${DL}/01/${GTIN}`, 'Digital Link in a DataMatrix'],
  ['scan', `]J0${DL}/01/${GTIN}`, 'Digital Link in a DotCode'],
  ['scan', `]Q0${DL}/01/${GTIN}`, 'unknown QR identifier'],
  ['scan', `]Q3${DL}/01/${GTIN}`, 'Digital Link in GS1 mode'],
  ['scan', `]Q1HTTPS://ID.GS1.ORG/01/${GTIN}`, 'upper-case scheme'],
  ['scan', `]Q1HtTps://id.gs1.org/01/${GTIN}`, 'mixed-case scheme'],
  ['scan', `]Q1plain text`, 'plain QR data'],
  ['scan', `]X0ABC`, 'unknown identifier'],
  ['scan', `]C`, 'short identifier'],
  // Bracketed AI data
  ['bracketed', `(01)${GTIN}(10)AB\\(C`, 'escaped bracket'],
  ['bracketed', `(10)AB\\(`, 'escaped bracket at the end'],
  ['bracketed', `(10)A)B`, 'closing bracket in a value'],
  ['bracketed', `(10)`, 'empty value at the end'],
  ['bracketed', `(10)(11)250131`, 'empty value'],
  ['bracketed', `(23)ABC`, 'retired AI'],
  ['bracketed', `(0)ABC`, 'one-digit AI'],
  ['bracketed', `(01234)ABC`, 'five-digit AI'],
  ['bracketed', `(10ABC`, 'unclosed AI'],
  ['bracketed', `(01)${GTIN} (10)ABC`, 'space in a value'],
  ['bracketed', `(01)${GTIN}(02)${GTIN}`, 'mutually exclusive (01) and (02)'],
  ['bracketed', `(01)${GTIN}(37)5`, 'mutually exclusive (01) and (37)'],
  ['bracketed', `(3100)000123(3101)000123`, 'two (310n) net weights'],
  ['bracketed', `(3100)000123(3100)000123`, 'repeated AI, same value'],
  ['bracketed', `(3100)000123(3100)000124`, 'repeated AI, different values'],
  ['bracketed', `(01)${GTIN}(10)A(01)${GTIN}`, 'repeated (01), same value'],
  ['bracketed', `(01)${GTIN}(21)S1(235)TPX`, 'mutually exclusive (21) and (235)'],
  ['bracketed', `(3920)123(3930)978123`, 'mutually exclusive (392n) and (393n)'],
  ['bracketed', `(4330)012345(4331)012345`, 'mutually exclusive temperatures'],
  ['bracketed', `(4330)012345-`, 'optional hyphen component'],
  ['bracketed', `(4330)012345+`, 'optional component, wrong character'],
  ['bracketed', `(8008)25013112`, 'production time without minutes'],
  ['bracketed', `(8008)2501311259`, 'production time with minutes'],
  ['bracketed', `(8008)2501311260`, 'production time, minute 60'],
  ['bracketed', `(253)${GDTI}`, 'GDTI without serial'],
  ['bracketed', `(253)${GDTI}(8030)ABCD`, 'digital signature with an unserialised GDTI'],
  ['bracketed', `(253)${GDTI}SER(8030)ABCD`, 'digital signature with a serialised GDTI'],
  ['bracketed', `(255)${GDTI}(8030)ABCD`, 'digital signature with a GCN without serial'],
  ['bracketed', `(8003)0${GDTI}(8030)ABCD`, 'digital signature with an unserialised GRAI'],
  ['bracketed', `(8003)0${GDTI}SER(8030)ABCD`, 'digital signature with a serialised GRAI'],
  // Dates
  ['bracketed', `(17)250100`, 'expiry, day 00'],
  ['bracketed', `(15)250200`, 'best before, day 00'],
  ['bracketed', `(11)250100`, 'production date, day 00'],
  ['bracketed', `(17)250132`, 'expiry, day 32'],
  ['bracketed', `(17)251301`, 'expiry, month 13'],
  ['bracketed', `(17)250001`, 'expiry, month 00'],
  ['bracketed', `(17)240229`, 'expiry, 29 February in a leap year'],
  ['bracketed', `(17)250229`, 'expiry, 29 February in a common year'],
  ['bracketed', `(17)000229`, 'expiry, 29 February 2000'],
  ['bracketed', `(7006)250100`, 'first freeze date, day 00 (not allowed)'],
  ['bracketed', `(7007)250101250131`, 'harvest date range'],
  ['bracketed', `(7007)250101`, 'harvest date without the range end'],
  ['bracketed', `(7007)2501012501`, 'harvest date with a partial range end'],
  ['bracketed', `(7003)2501312359`, 'expiry date and time'],
  ['bracketed', `(7003)2501312460`, 'expiry date and time, hour 24'],
  ['bracketed', `(4324)2501312359`, 'not before delivery date and time'],
  ['bracketed', `(7250)20240229`, 'date of birth'],
  ['bracketed', `(7250)20250229`, 'date of birth, 29 February in a common year'],
  ['elementString', `17250100`, 'expiry day 00 as raw data'],
  ['scan', `]d21725010010LOT`, 'expiry day 00 in a DataMatrix'],
  // GS1 Digital Link: schemes, domains and characters
  ['digitalLink', `${DL}/01/${GTIN}`, 'canonical'],
  ['digitalLink', `http://id.gs1.org/01/${GTIN}`, 'http'],
  ['digitalLink', `HTTPS://ID.GS1.ORG/01/${GTIN}`, 'upper-case scheme'],
  ['digitalLink', `HTTP://example.com/01/${GTIN}`, 'upper-case http scheme'],
  ['digitalLink', `Https://id.gs1.org/01/${GTIN}`, 'mixed-case scheme'],
  ['digitalLink', `ftp://id.gs1.org/01/${GTIN}`, 'ftp scheme'],
  ['digitalLink', `https:///01/${GTIN}`, 'empty domain'],
  ['digitalLink', `https://example.com`, 'no path'],
  ['digitalLink', `https://example.com/`, 'empty path'],
  ['digitalLink', `https://exa_mple.com/01/${GTIN}`, 'underscore in the domain'],
  ['digitalLink', `https://user@example.com/01/${GTIN}`, 'user info in the domain'],
  ['digitalLink', `https://example.com:8443/01/${GTIN}`, 'port'],
  ['digitalLink', `https://[2001:db8::1]/01/${GTIN}`, 'IPv6 literal'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A B`, 'space'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A"B`, 'double quote'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A^B`, 'caret'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A{B}`, 'braces'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A|B`, 'vertical bar'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/A\\B`, 'backslash'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/AÄB`, 'non-ASCII character'],
  // Path prefixes and the primary key
  ['digitalLink', `https://example.com/some/path/01/${GTIN}`, 'path prefix'],
  ['digitalLink', `https://example.com/a/b/c/01/${GTIN}/10/LOT`, 'odd path prefix'],
  ['digitalLink', `https://example.com//01/${GTIN}`, 'empty segment before the key'],
  ['digitalLink', `https://example.com/01/${GTIN}/`, 'trailing slash'],
  ['digitalLink', `https://example.com/01/${GTIN}//`, 'two trailing slashes'],
  ['digitalLink', `https://example.com/01//10/LOT`, 'empty key value'],
  ['digitalLink', `https://example.com/01/${GTIN}/10/`, 'empty qualifier value'],
  ['digitalLink', `https://example.com/00/${SSCC}/01/${GTIN}`, 'two primary keys'],
  ['digitalLink', `https://example.com/01/${GTIN}/00/${SSCC}`, 'SSCC after a GTIN'],
  ['digitalLink', `https://example.com/10/LOT/01/${GTIN}`, 'qualifier before the key'],
  ['digitalLink', `https://example.com/01/${GTIN}/foo/bar`, 'non-AI segments after the key'],
  ['digitalLink', `https://example.com/gtin/${GTIN}`, 'convenience alpha'],
  ['digitalLink', `https://example.com/01/${GTIN.slice(1)}`, 'GTIN-13 (zero-suppressed)'],
  ['digitalLink', `https://example.com/01/${GTIN.slice(0, 13)}4`, 'GTIN with a wrong check digit'],
  ['digitalLink', `https://example.com/%30%31/${GTIN}`, 'percent-encoded AI'],
  ['digitalLink', `${DL}/00/${SSCC}`, 'SSCC'],
  ['digitalLink', `${DL}/00/${SSCC}/10/LOT`, 'SSCC takes no qualifiers'],
  ['digitalLink', `${DL}/414/${GDTI}/254/EXT1`, 'GLN with extension'],
  ['digitalLink', `${DL}/417/${GDTI}`, 'party GLN'],
  ['digitalLink', `${DL}/8004/9521234ABC123`, 'GIAI'],
  ['digitalLink', `${DL}/8006/${GTIN}0102/22/CPV/10/LOT/21/S`, 'ITIP with qualifiers'],
  ['digitalLink', `${DL}/8013/1987654Ad4X4bL5ttr2310c2K/21/S1`, 'GMN with serial'],
  ['digitalLink', `${DL}/253/${GDTI}SER`, 'GDTI'],
  ['digitalLink', `${DL}/8010/0952%2FAB%23/8011/123`, 'CPID with serial'],
  // Qualifier order
  ['digitalLink', `${DL}/01/${GTIN}/22/CPV/10/LOT/21/SER`, 'all three qualifiers in order'],
  ['digitalLink', `${DL}/01/${GTIN}/22/CPV/21/SER`, 'qualifiers with a gap'],
  ['digitalLink', `${DL}/01/${GTIN}/10/LOT/22/CPV`, 'qualifiers out of order'],
  ['digitalLink', `${DL}/01/${GTIN}/21/SER/10/LOT`, 'serial before lot'],
  ['digitalLink', `${DL}/01/${GTIN}/235/TPX`, 'third-party controlled extension'],
  ['digitalLink', `${DL}/01/${GTIN}/235/TPX/21/SER`, 'TPX with serial'],
  ['digitalLink', `${DL}/01/${GTIN}/10/LOT/10/LOT`, 'repeated qualifier'],
  ['digitalLink', `${DL}/01/${GTIN}/17/250131`, 'data attribute in the path'],
  // Query attributes
  ['digitalLink', `${DL}/01/${GTIN}?17=250131`, 'expiry attribute'],
  ['digitalLink', `${DL}/01/${GTIN}?17=250100`, 'expiry attribute, day 00'],
  ['digitalLink', `${DL}/01/${GTIN}?3103=000123&17=250131`, 'two attributes'],
  ['digitalLink', `${DL}/01/${GTIN}/10/LOT?17=250131&linktype=all`, 'attribute and non-AI parameter'],
  ['digitalLink', `${DL}/01/${GTIN}?linktype=all`, 'only a non-AI parameter'],
  ['digitalLink', `${DL}/01/${GTIN}?singleton`, 'parameter without a value'],
  ['digitalLink', `${DL}/01/${GTIN}?17`, 'AI parameter without a value'],
  ['digitalLink', `${DL}/01/${GTIN}?17=`, 'empty attribute value'],
  ['digitalLink', `${DL}/01/${GTIN}?=250131`, 'parameter without a name'],
  ['digitalLink', `${DL}/01/${GTIN}?&&17=250131&`, 'empty parameters'],
  ['digitalLink', `${DL}/01/${GTIN}?17=250131&17=250131`, 'duplicate attribute'],
  ['digitalLink', `${DL}/01/${GTIN}/10/LOT?10=LOT`, 'attribute duplicates a qualifier'],
  ['digitalLink', `${DL}/01/${GTIN}?10=LOT`, 'qualifier as an attribute'],
  ['digitalLink', `${DL}/01/${GTIN}/10/LOT?21=SER`, 'serial as an attribute'],
  ['digitalLink', `${DL}/01/${GTIN}/21/SER?10=LOT`, 'lot as an attribute after a serial'],
  ['digitalLink', `${DL}/01/${GTIN}/235/TPX?21=SER`, 'serial as an attribute with a TPX'],
  ['digitalLink', `${DL}/01/${GTIN}?9999=1`, 'unknown numeric AI'],
  ['digitalLink', `${DL}/01/${GTIN}?01=${GTIN}`, 'primary key repeated as an attribute'],
  ['digitalLink', `${DL}/01/${GTIN}?00=${SSCC}`, 'another primary key as an attribute'],
  ['digitalLink', `${DL}/00/${SSCC}?01=${GTIN}`, 'GTIN as an attribute of an SSCC'],
  ['digitalLink', `${DL}/00/${SSCC}?02=${GTIN}&37=12`, 'content attributes of an SSCC'],
  ['digitalLink', `${DL}/01/${GTIN}?02=${GTIN}`, 'attribute exclusive with the key'],
  ['digitalLink', `${DL}/01/${GTIN}?8200=https%3A%2F%2Fexample.com`, 'AI that is not a data attribute'],
  ['digitalLink', `${DL}/01/${GTIN}?99=INT&91=CO`, 'company internal attributes'],
  ['digitalLink', `${DL}/01/${GTIN}?4300=A%20B`, 'percent-encoded space in a pcenc attribute'],
  ['digitalLink', `${DL}/01/${GTIN}#fragment`, 'fragment'],
  ['digitalLink', `${DL}/01/${GTIN}?17=250131#frag?99=X`, 'query and fragment'],
  ['digitalLink', `${DL}/01/${GTIN}#/10/LOT`, 'path-like fragment'],
  // Percent-encoding
  ['digitalLink', `${DL}/01/${GTIN}/10/A%2FB`, 'encoded slash'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%2fB`, 'lower-case hex'],
  ['digitalLink', `${DL}/01/${GTIN}/10/%41%42%43`, 'encoded letters'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%25B`, 'encoded percent sign'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%B`, 'percent sign followed by one character'],
  ['digitalLink', `${DL}/01/${GTIN}/10/AB%`, 'trailing percent sign'],
  ['digitalLink', `${DL}/01/${GTIN}/10/AB%4`, 'truncated escape'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%G1`, 'invalid hex digit'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%00B`, 'encoded NUL'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%20B`, 'encoded space'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%7EB`, 'encoded tilde'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%1DB`, 'encoded GS'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%5EB`, 'encoded caret'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A%C3%84`, 'encoded UTF-8'],
  ['digitalLink', `${DL}/01/${GTIN}/10/A+B`, 'plus in the path'],
  ['digitalLink', `${DL}/01/${GTIN}?99=A+B`, 'plus in the query (a space)'],
  ['digitalLink', `${DL}/01/${GTIN}?99=A%2BB`, 'encoded plus in the query'],
  ['digitalLink', `${DL}/01/${GTIN}?99=A%26B%3DC`, 'encoded ampersand and equals sign'],
  ['digitalLink', `${DL}/01/${GTIN}/21/A%2F%2FB%3F%23`, 'encoded delimiters in a serial'],
  ['digitalLink', `${DL}/01/${GTIN}?17=25013%31`, 'encoded digit in a date'],
];

// ---------------------------------------------------------------------------------------------------------------
// Assembly
// ---------------------------------------------------------------------------------------------------------------

const cases = [];
const seen = new Set();
function add(kind, input, source) {
  const key = `${kind}\u0000${input}`;
  if (seen.has(key)) return;
  seen.add(key);
  cases.push({ kind, input, source, ...evaluate(kind, input) });
}

// 1. The engine's test vectors.
const vectors = vectorInputs();
for (const v of vectors) for (const c of normalise(v)) add(c.kind, c.input, c.source);

// 2. Every AI of the dictionary.
const vectorValues = new Map();
for (const c of cases) {
  if (c.kind !== 'bracketed' || !c.valid) continue;
  for (const [ai, value] of c.elements) {
    if (!vectorValues.has(ai)) vectorValues.set(ai, []);
    vectorValues.get(ai).push(value);
  }
}
const dictionary = readDictionary();
const symbologies = [']C1', ']d2', ']Q3', ']e0', ']J1'];
const missing = [];
dictionary.forEach((entry, i) => {
  const value = candidateValues(entry, vectorValues).find((v) => evaluate('bracketed', `(${entry.ai})${v}`).valid);
  if (value === undefined) { missing.push(entry.ai); return; }
  const source = `dictionary (${entry.ai})`;
  const tail = entry.ai === '99' ? '98CORPUS' : '99CORPUS';
  const separator = entry.predefined ? '' : GS;
  add('bracketed', `(${entry.ai})${value}`, source);
  if (i % 2 === 0) add('scan', `${symbologies[(i / 2) % symbologies.length]}${entry.ai}${value}${separator}${tail}`, source);
  else add('elementString', `${i % 4 === 1 ? GS : ''}${entry.ai}${value}${i % 3 === 0 ? '' : separator + tail}`, source);
  if (entry.dlKey) add('digitalLink', `${DL}/${entry.ai}/${uriEscape(value)}`, source);
  else if (entry.dlAttribute || i % 5 === 0) add('digitalLink', `${DL}/01/${GTIN}?${entry.ai}=${uriEscape(value)}`, source);
  // A broken variant: one character too many, one too few, or a character outside the AI's character set.
  const last = entry.components[entry.components.length - 1];
  const broken = [
    value + (last.charset === 'N' ? '7' : 'Z').repeat(Math.max(1, entry.components.reduce((n, c) => n + c.max, 0) - value.length + 1)),
    value.slice(0, -1),
    value.slice(0, -1) + (entry.components[0].charset === 'N' && entry.components.length === 1 ? 'A' : '~'),
  ][i % 3];
  const kinds = ['bracketed', 'scan', 'elementString'];
  const kind = kinds[Math.floor(i / 3) % 3];
  if (kind === 'bracketed') add(kind, `(${entry.ai})${broken}`, `${source}, broken`);
  else if (kind === 'scan') add(kind, `${symbologies[i % symbologies.length]}${entry.ai}${broken}`, `${source}, broken`);
  else add(kind, `${entry.ai}${broken}`, `${source}, broken`);
});
if (missing.length) throw new Error(`no valid value found for AI(s) ${missing.join(', ')}: add a candidate`);

// 3. The hand-written cases.
for (const [kind, input, note] of special) add(kind, input, note);

// ---------------------------------------------------------------------------------------------------------------
// Output: a header, then one case per line
// ---------------------------------------------------------------------------------------------------------------

const counts = {};
for (const c of cases) {
  counts[c.kind] ??= { valid: 0, invalid: 0 };
  counts[c.kind][c.valid ? 'valid' : 'invalid'] += 1;
}
const header = {
  description: 'GS1 parsing corpus: each case is an input to our GS1 parsers with the result GS1\'s Barcode Syntax Engine gives for it. '
    + 'Valid cases list the (AI, value) elements in order; invalid ones must be rejected.',
  engine: {
    name: 'GS1 Barcode Syntax Engine (https://github.com/gs1/gs1-syntax-engine), WebAssembly build',
    version: engine.version,
    settings: 'RequisiteAIs validation off (AI associations are judged over all carriers on an item, not per barcode); '
      + 'every other option at its default: MutexAIs, RepeatedAIs, DigSigSerialKey and UnknownAInotDLattr on, '
      + 'permitUnknownAIs, permitZeroSuppressedGTINinDLuris and addCheckDigit off',
  },
  generated: {
    by: 'node tools/gs1/build-corpus.mjs <gs1encoder.mjs> <engine src/c-lib>',
    date: new Date().toISOString().slice(0, 10),
    dictionary: 'tools/gs1/gs1-syntax-dictionary.txt',
    sources: 'inputs of the engine\'s unit-test vectors (ai.c, dl.c, scandata.c), one valid element string per dictionary AI '
      + 'with input-form and broken variants, and hand-written cases (see the script)',
  },
  kinds: {
    scan: 'scan data with an AIM symbology identifier (]C1, ]e0, ]d2, ]Q3, ]J1 AI data; ]E0/]E4 EAN; ]Q1, ]d1, ]J0 plain data '
      + 'or a Digital Link URI); engine: scanData. EAN-13/EAN-8 primary data is listed as (01), padded to 14 digits',
    elementString: 'AI data without symbology identifier, GS (\\u001d) as FNC1, optionally a leading GS; engine: dataStr "^" + data',
    bracketed: 'bracketed AI data, (AI)value, a literal "(" written "\\("; engine: aiDataStr',
    digitalLink: 'a GS1 Digital Link URI; engine: dataStr',
  },
  counts: { total: cases.length, ...counts },
};
const lines = cases.map((c, i) => '    ' + JSON.stringify({ id: i + 1, ...c }));
const json = JSON.stringify(header, null, 2).replace(/\n}$/, ',\n  "cases": [\n' + lines.join(',\n') + '\n  ]\n}\n');
JSON.parse(json);
fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, json);
console.log(`wrote ${path.relative(process.cwd(), outPath)}: ${cases.length} cases, engine ${engine.version}`);
console.log(JSON.stringify(counts));
if (hriDisagreements.length) console.log(`AI data and HRI differ for ${hriDisagreements.length} input(s):\n  ${hriDisagreements.slice(0, 10).join('\n  ')}`);
