// One-off migration for #467 (D1, D4, D6, D24): moves every translated text out of an app's App_Data JSON
// into its translations.json, under the convention keys the runtime resolves. Idempotent.
//   node tools/migrate-467-translations.mjs apps/Fleet/Fleet [more app dirs...]
import fs from 'node:fs';
import path from 'node:path';

const BOM = '﻿';

const isText = v => v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length > 0
  && Object.values(v).every(x => typeof x === 'string');

const humanize = name => {
  const last = name.slice(name.lastIndexOf('.') + 1);
  let out = last.charAt(0).toUpperCase();
  for (let i = 1; i < last.length; i++) {
    const c = last[i], p = last[i - 1];
    if (c >= 'A' && c <= 'Z' && !(p >= 'A' && p <= 'Z') && p !== ' ') out += ' ';
    out += c;
  }
  return out;
};

const camel = text => text.normalize('NFD').replace(/[̀-ͯ]/g, '')
  .replace(/[^A-Za-z0-9]+(.)?/g, (_, c) => (c ? c.toUpperCase() : ''))
  .replace(/^[A-Z]+(?=[A-Z][a-z]|$)|^./, c => c.toLowerCase());

function readJson(file) {
  const raw = fs.readFileSync(file, 'utf8');
  const bom = raw.startsWith(BOM);
  return { data: JSON.parse(bom ? raw.slice(1) : raw), eol: raw.includes('\r\n') ? '\r\n' : '\n', bom, final: raw.endsWith('\n') };
}
function writeJson(file, { data, eol, bom, final }) {
  fs.writeFileSync(file, (bom ? BOM : '') + JSON.stringify(data, null, 2).replace(/\n/g, eol) + (final === false ? '' : eol));
}

/** Inserts text under a dotted key, nested; an existing value in the app file wins per language. */
function put(translations, key, text, { skipIfHumanized } = {}) {
  if (!isText(text)) return;
  const segments = key.split('.');
  if (skipIfHumanized && Object.keys(text).length === 1 && text.en === humanize(skipIfHumanized)) return;
  let node = translations;
  for (const s of segments.slice(0, -1)) {
    if (node[s] === undefined) node[s] = {};
    if (isText(node[s])) throw new Error(`'${key}': '${s}' is already a translation (SPARK_TRANS_002)`);
    node = node[s];
  }
  const leaf = segments.at(-1);
  node[leaf] = { ...text, ...(node[leaf] ?? {}) };
  for (const [lang, value] of Object.entries(text)) {
    if (!node[leaf][lang]) node[leaf][lang] = value;
  }
}

function migrateApp(appDir) {
  const appData = path.join(appDir, 'App_Data');
  const translationsFile = path.join(appData, 'translations.json');
  const translations = fs.existsSync(translationsFile)
    ? readJson(translationsFile)
    : { data: {}, eol: '\r\n', bom: false };
  const t = translations.data;
  const coreLanguageNames = { en: 'English', fr: 'French', nl: 'Dutch', de: 'German', es: 'Spanish' };

  // Model files
  const modelDir = path.join(appData, 'Model');
  for (const name of fs.existsSync(modelDir) ? fs.readdirSync(modelDir).filter(f => f.endsWith('.json')) : []) {
    const file = path.join(modelDir, name);
    const json = readJson(file);
    const po = json.data.persistentObject;
    if (!po) continue;
    const prefix = `model.${po.name}`;
    if (isText(po.description)) { put(t, `${prefix}.label`, po.description, { skipIfHumanized: po.name }); delete po.description; }
    if (isText(po.label)) { put(t, `${prefix}.label`, po.label, { skipIfHumanized: po.name }); delete po.label; }
    for (const a of po.attributes ?? []) {
      if (isText(a.label)) { put(t, `${prefix}.attributes.${a.name}.label`, a.label, { skipIfHumanized: a.name }); delete a.label; }
      if (isText(a.description)) { put(t, `${prefix}.attributes.${a.name}.description`, a.description); delete a.description; }
      const used = new Set();
      for (const rule of a.rules ?? []) {
        if (!isText(rule.message)) continue;
        let key = `${prefix}.attributes.${a.name}.rules.${rule.type}`;
        for (let n = 2; used.has(key); n++) key = `${prefix}.attributes.${a.name}.rules.${rule.type}${n}`;
        used.add(key);
        put(t, key, rule.message);
        rule.message = key;
      }
    }
    for (const tab of po.tabs ?? []) {
      if (isText(tab.label)) { put(t, `${prefix}.tabs.${tab.name}.label`, tab.label, { skipIfHumanized: tab.name }); delete tab.label; }
    }
    for (const g of po.groups ?? []) {
      if (isText(g.label)) { put(t, `${prefix}.groups.${g.name}.label`, g.label, { skipIfHumanized: g.name }); delete g.label; }
    }
    for (const q of json.data.queries ?? []) {
      if (isText(q.description)) { put(t, `queries.${q.name}.label`, q.description, { skipIfHumanized: q.name }); delete q.description; }
      if (isText(q.label)) { put(t, `queries.${q.name}.label`, q.label, { skipIfHumanized: q.name }); delete q.label; }
    }
    writeJson(file, json);
  }

  // programUnits.json: name → key
  const puFile = path.join(appData, 'programUnits.json');
  if (fs.existsSync(puFile)) {
    const json = readJson(puFile);
    // Keys in document order (a group's name precedes its units), replaced textually so a
    // hand-aligned file keeps its layout.
    const keys = [];
    for (const group of json.data.programUnitGroups ?? []) {
      if (isText(group.name)) {
        const key = `programUnits.groups.${camel(group.name.en ?? Object.values(group.name)[0])}`;
        put(t, key, group.name);
        keys.push(key);
      }
      for (const unit of group.programUnits ?? []) {
        if (isText(unit.name)) {
          const key = `programUnits.${unit.alias ? camel(unit.alias) : camel(unit.name.en ?? Object.values(unit.name)[0])}`;
          put(t, key, unit.name);
          keys.push(key);
        }
      }
    }
    let i = 0;
    const raw = fs.readFileSync(puFile, 'utf8');
    const text = raw.replace(/("name"\s*:\s*)\{[^{}]*\}/g, (_, lead) => `${lead}"${keys[i++]}"`);
    if (i !== keys.length) throw new Error(`${puFile}: replaced ${i} names, expected ${keys.length}`);
    fs.writeFileSync(puFile, text);
  }

  // security.json: group → untranslated name (D24); label under security.groups.{name}.label
  const secFile = path.join(appData, 'security.json');
  if (fs.existsSync(secFile)) {
    const json = readJson(secFile);
    let text = fs.readFileSync(secFile, 'utf8');
    for (const [id, value] of Object.entries(json.data.groups ?? {})) {
      if (!isText(value)) continue;
      const groupName = value.en ?? Object.values(value)[0];
      if (groupName.includes('.')) throw new Error(`security group '${groupName}' contains '.', which a translation key cannot hold`);
      put(t, `security.groups.${groupName}.label`, value, { skipIfHumanized: groupName });
      // Textual, so a hand-aligned file keeps its layout.
      const pattern = new RegExp(`("${id}"\\s*:\\s*)\\{[^{}]*\\}`);
      if (!pattern.test(text)) throw new Error(`${secFile}: group ${id} not found textually`);
      text = text.replace(pattern, (_, lead) => `${lead}${JSON.stringify(groupName)}`);
    }
    if (json.data.groupComments) throw new Error(`${secFile}: groupComments was removed (#467, D24); migrate by hand`);
    fs.writeFileSync(secFile, text);
  }

  // culture.json: languages → array of codes; names only when they differ from the core's
  const cultureFile = path.join(appData, 'culture.json');
  if (fs.existsSync(cultureFile)) {
    const json = readJson(cultureFile);
    const langs = json.data.languages;
    if (langs && !Array.isArray(langs)) {
      for (const [code, names] of Object.entries(langs)) {
        if (isText(names) && names.en !== coreLanguageNames[code]) put(t, `culture.languages.${code}`, names);
      }
      json.data.languages = Object.keys(langs);
      writeJson(cultureFile, json);
    }
  }

  // customActions.json → actions.json (M3, D7). displayName moves to actions.{Name}.label;
  // confirmationMessageKey becomes the explicit `confirmation` key. A description that is user-facing
  // text moves to actions.{Name}.description; one that explains the configuration to a developer
  // (it talks about showedOn / selectionRule) is dropped, since the action's C# class documents it.
  const oldActionsFile = path.join(appData, 'customActions.json');
  if (fs.existsSync(oldActionsFile)) {
    const json = readJson(oldActionsFile);
    for (const [name, definition] of Object.entries(json.data)) {
      if (!definition || typeof definition !== 'object') continue;
      const migrated = {};
      for (const [property, value] of Object.entries(definition)) {
        if (property === 'displayName') put(t, `actions.${name}.label`, value, { skipIfHumanized: name });
        else if (property === 'confirmationMessageKey') migrated.confirmation = value;
        else if (property === 'description') {
          if (typeof value === 'string' && !/showedOn|selectionRule/.test(value)) put(t, `actions.${name}.description`, { en: value });
        }
        else migrated[property] = value;
      }
      json.data[name] = migrated;
    }
    writeJson(path.join(appData, 'actions.json'), json);
    fs.unlinkSync(oldActionsFile);
  }

  writeJson(translationsFile, translations);

  // Report anything translated that is still embedded.
  for (const file of walk(appData)) {
    if (/translations\.json$/i.test(file)) continue;
    const left = (fs.readFileSync(file, 'utf8').match(/"en"\s*:/g) ?? []).length;
    if (left) console.log(`  still embedded: ${left} "en" in ${path.relative(appDir, file)}`);
  }
  console.log(`migrated ${appDir}`);
}

function* walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full);
    else if (entry.name.endsWith('.json')) yield full;
  }
}

for (const app of process.argv.slice(2)) migrateApp(app);
