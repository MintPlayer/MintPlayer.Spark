import { promises as fs } from 'node:fs';
import type { PlanItem } from '../../contracts/types';
import { EditRefused, escapeRegExp, findLines, replaceUniqueToken, type TextDoc } from '../../core/lineEdit';
import { combined } from '../../util/exec';
import { basename, directoryFiles } from '../../util/repoFiles';
import { OK, dirOf, exists, notPinned, outcome, readDoc, rel2abs, tokenRegExp, writeDoc, type Adapter, type AdapterContext } from '../adapter';
import { gradleFailed, parseGradle, parseMaven } from '../blockers';

/**
 * Maven and Gradle. Both evaluate repository code on every invocation (S13/S14: Maven loads plugins
 * and extensions, Gradle runs build.gradle(.kts)), so these jobs never get feed secrets (D-S13d,
 * Gradle never per D-S14b). The edit is ours either way; the tool only resolves/locks.
 */

function splitCoordinates(depName: string): { group: string; artifact: string } {
  const [group, artifact] = depName.split(':');
  return { group, artifact: artifact ?? group };
}

/**
 * pom.xml: a property (`<guava.version>31.0-jre</guava.version>`, the record's sharedVariableName) or
 * the `<version>` of the one `<dependency>/<plugin>/<parent>/<extension>` block naming groupId and
 * artifactId.
 */
export function editPom(doc: TextDoc, file: string, item: PlanItem): void {
  const cur = escapeRegExp(item.currentValue);
  if (item.sharedVariableName) {
    const prop = escapeRegExp(item.sharedVariableName);
    replaceUniqueToken(doc, { find: new RegExp(`<${prop}>(?<v>${cur})</${prop}>`), to: item.newValue }, file);
    return;
  }
  const { group, artifact } = splitCoordinates(item.depName);
  const blocks: Array<[number, number]> = [];
  for (const a of findLines(doc, new RegExp(`<artifactId>\\s*${escapeRegExp(artifact)}\\s*</artifactId>`))) {
    let start = a;
    while (start > 0 && !/<(dependency|plugin|parent|extension)>/.test(doc.lines[start].text)) start--;
    let end = a;
    while (end < doc.lines.length - 1 && !/<\/(dependency|plugin|parent|extension)>/.test(doc.lines[end].text)) end++;
    const text = doc.lines.slice(start, end + 1).map((l) => l.text).join('\n');
    const groupOk = new RegExp(`<groupId>\\s*${escapeRegExp(group)}\\s*</groupId>`).test(text) || (/<plugin>/.test(text) && group === 'org.apache.maven.plugins');
    if (groupOk) blocks.push([start, end + 1]);
  }
  const find = new RegExp(`<version>(?<v>${cur})</version>`);
  const candidates = blocks.filter(([s, e]) => doc.lines.slice(s, e).some((l) => find.test(l.text)));
  if (candidates.length !== 1) throw new EditRefused(file, `${item.depName} <version>${item.currentValue}</version>`, candidates.length);
  replaceUniqueToken(doc, { find, to: item.newValue, range: candidates[0] }, file);
}

async function maven(ctx: AdapterContext, dir: string, args: string[]) {
  const wrapper = await exists(ctx, dir === '.' ? 'mvnw' : `${dir}/mvnw`);
  return ctx.exec(wrapper ? './mvnw' : 'mvn', ['-B', '-ntp', ...args], { cwd: rel2abs(ctx, dir), timeoutMs: 20 * 60_000 });
}

export const mavenAdapter: Adapter = {
  id: 'maven',
  matches: (item) => item.datasource === 'maven' && basename(item.packageFile) === 'pom.xml',
  files: async (_ctx, item) => [item.packageFile],
  async edit(ctx, item) {
    const doc = await readDoc(ctx, item.packageFile);
    editPom(doc, item.packageFile, item);
    await writeDoc(ctx, item.packageFile, doc);
  },
  async verify(ctx, items) {
    // No lockfile: resolve once so a missing artifact becomes a blocker (S13 maven textedit-inline).
    for (const dir of new Set(items.map((i) => dirOf(i.packageFile)))) {
      const r = await maven(ctx, dir, ['dependency:resolve', '-DincludeScope=runtime']);
      if (r.code !== 0) return outcome(r);
    }
    return OK;
  },
  parseBlockers: (output) => parseMaven(output),
  // ./mvnw when the repo has it; a missing mvn surfaces as exit 127 (a 'tool' failure).
  tools: () => [],
};

/**
 * Gradle: libs.versions.toml (`[versions] key = "x"` via sharedVariableName, or an inline
 * `version = "x"` on the library line), build.gradle(.kts) (`"g:a:x"` or an ext property) and
 * gradle.properties (`key=x`).
 */
export function editGradle(doc: TextDoc, file: string, item: PlanItem): void {
  const { group, artifact } = splitCoordinates(item.depName);
  const cur = item.currentValue;
  if (item.sharedVariableName) {
    const key = escapeRegExp(item.sharedVariableName);
    const find = new RegExp(`^\\s*(?:val\\s+|def\\s+|ext\\.)?${key}\\s*[=:]\\s*["']?(?<v>${escapeRegExp(cur)})["']?`);
    replaceUniqueToken(doc, { find, to: item.newValue }, file);
    return;
  }
  const coordinates = `${group}:${artifact}`;
  const named = (t: string) =>
    t.includes(`${coordinates}:`) || t.includes(`"${coordinates}"`) || (t.includes(`"${group}"`) && t.includes(`"${artifact}"`)) || t.includes(`'${coordinates}`);
  replaceUniqueToken(doc, { find: tokenRegExp(cur), to: item.newValue, lineFilter: named }, file);
}

async function gradleLockfiles(ctx: AdapterContext): Promise<string[]> {
  return (await directoryFiles(ctx.root).list()).filter((f) => basename(f) === 'gradle.lockfile' || /\.lockfile$/.test(f) && f.includes('gradle/dependency-locks/'));
}

async function gradle(ctx: AdapterContext, args: string[]) {
  const wrapper = await exists(ctx, 'gradlew');
  return ctx.exec(wrapper ? './gradlew' : 'gradle', ['--no-daemon', '--console=plain', ...args], { cwd: ctx.root, timeoutMs: 30 * 60_000 });
}

export const gradleAdapter: Adapter = {
  id: 'gradle',
  matches: (item) =>
    item.datasource === 'maven' && (item.manager === 'gradle' || /\.gradle(\.kts)?$|libs\.versions\.toml$|gradle\.properties$/.test(item.packageFile)),
  files: async (ctx, item) => [item.packageFile, ...(await gradleLockfiles(ctx))],
  async edit(ctx, item) {
    const doc = await readDoc(ctx, item.packageFile);
    editGradle(doc, item.packageFile, item);
    await writeDoc(ctx, item.packageFile, doc);
  },
  async relock(ctx) {
    // S13: `dependencies --write-locks`; Gradle exits 0 while printing FAILED, so scan the output.
    const locks = await gradleLockfiles(ctx);
    const r = await gradle(ctx, ['dependencies', ...(locks.length ? ['--write-locks'] : [])]);
    const text = combined(r);
    if (r.code !== 0 || gradleFailed(text)) return { ok: false, output: text, kind: r.code === 127 ? 'tool' : 'resolver' };
    return OK;
  },
  async verify(ctx, items) {
    const locks = await gradleLockfiles(ctx);
    if (locks.length === 0) return OK;
    const texts = await Promise.all(locks.map((l) => fs.readFile(rel2abs(ctx, l), 'utf8')));
    const misses = items.filter((i) => !texts.some((t) => t.includes(`${i.depName}:${i.newVersion}=`))).map((i) => `${i.depName}:${i.newVersion}`);
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parseGradle(output),
  tools: () => [],
};
