import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { Blocker, PlanItem } from '../../contracts/types';
import { EditRefused, blockFrom, escapeRegExp, findLines, replaceUniqueToken, type TextDoc } from '../../core/lineEdit';
import { basename, directoryFiles } from '../../util/repoFiles';
import { OK, dirOf, notPinned, outcome, readDoc, rel2abs, writeDoc, type Adapter, type AdapterContext, type StepOutcome } from '../adapter';
import { parseNuGetAssets, parseNuGetText } from '../blockers';

/**
 * NuGet: PackageReference in a project, Central Package Management (Directory.Packages.props),
 * .config/dotnet-tools.json and global.json msbuild-sdks. S13: `dotnet add package` and
 * `dotnet tool update` reformat XML/JSON (CPM: the whole props file), so we text-edit and then only
 * RESTORE. D-S14a: restore runs MSBuild evaluation but never a package's build/*.targets; nothing in
 * this job builds.
 */

const DOTNET_ENV = { DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false' };

type Kind = 'project' | 'props' | 'tools' | 'global.json';

function kindOf(packageFile: string): Kind | null {
  const b = basename(packageFile);
  if (/\.(cs|fs|vb)proj$/i.test(b) || b === 'Directory.Build.props' || b === 'Directory.Build.targets') return 'project';
  if (b === 'Directory.Packages.props' || b === 'Packages.props' || /\.props$|\.targets$/i.test(b)) return 'props';
  if (b === 'dotnet-tools.json') return 'tools';
  if (b === 'global.json') return 'global.json';
  return null;
}

/**
 * The version token of one package in an MSBuild file. Anchors are the `Include=`/`Update=` lines of
 * PackageReference/PackageVersion/GlobalPackageReference; the token is an attribute on the anchor
 * line (Version=, VersionOverride=) or a child <Version> element in the anchor's block (S13 case
 * `Serilog` with a child element). All anchors together must yield exactly one token.
 */
export function editMsBuild(doc: TextDoc, file: string, item: PlanItem): void {
  const id = escapeRegExp(item.depName);
  const cur = escapeRegExp(item.currentValue);
  const anchors = findLines(doc, new RegExp(`<(PackageReference|PackageVersion|GlobalPackageReference)\\b[^>]*\\b(Include|Update)\\s*=\\s*"${id}"`, 'i'));
  const hits: Array<{ range: [number, number]; find: RegExp }> = [];
  for (const a of anchors) {
    const attr = new RegExp(`\\b(Version|VersionOverride)\\s*=\\s*"(?<v>${cur})"`);
    if (attr.test(doc.lines[a].text)) {
      hits.push({ range: [a, a + 1], find: attr });
      continue;
    }
    const block = blockFrom(doc, a, /\/>|<\/(PackageReference|PackageVersion|GlobalPackageReference)>/);
    const child = new RegExp(`<(Version|VersionOverride)>(?<v>${cur})</(Version|VersionOverride)>`);
    for (let i = block[0]; i < block[1]; i++) if (child.test(doc.lines[i].text)) hits.push({ range: [i, i + 1], find: child });
  }
  if (hits.length !== 1) {
    throw new EditRefused(file, hits.length === 0 ? `no ${item.depName} version ${item.currentValue}` : `ambiguous ${item.depName} version ${item.currentValue}`, hits.length);
  }
  replaceUniqueToken(doc, { find: hits[0].find, to: item.newValue, range: hits[0].range }, file);
}

export function editDotnetTools(doc: TextDoc, file: string, item: PlanItem): void {
  const anchors = findLines(doc, new RegExp(`^\\s*"${escapeRegExp(item.depName)}"\\s*:\\s*\\{`, 'i'));
  if (anchors.length !== 1) throw new EditRefused(file, `tool ${item.depName}`, anchors.length);
  const block = blockFrom(doc, anchors[0], /^\s*}/);
  replaceUniqueToken(doc, { find: new RegExp(`"version"\\s*:\\s*"(?<v>${escapeRegExp(item.currentValue)})"`), to: item.newValue, range: block }, file);
}

export function editGlobalJsonSdk(doc: TextDoc, file: string, item: PlanItem): void {
  replaceUniqueToken(doc, { find: new RegExp(`"${escapeRegExp(item.depName)}"\\s*:\\s*"(?<v>${escapeRegExp(item.currentValue)})"`, 'i'), to: item.newValue }, file);
}

/** What to restore for a manifest: the project itself, or every project under a props file's directory. */
export async function restoreTargets(ctx: AdapterContext, packageFile: string): Promise<string[]> {
  const kind = kindOf(packageFile);
  if (kind === 'project' && /proj$/i.test(packageFile)) return [packageFile];
  const dir = dirOf(packageFile);
  const all = await directoryFiles(ctx.root).list();
  const inDir = (f: string) => dir === '.' || f.startsWith(`${dir}/`);
  const solutions = all.filter((f) => inDir(f) && /\.slnx?$/i.test(f) && dirOf(f) === dir);
  if (solutions.length > 0) return solutions;
  return all.filter((f) => inDir(f) && /\.(cs|fs|vb)proj$/i.test(f));
}

async function lockFilesFor(ctx: AdapterContext, packageFile: string): Promise<string[]> {
  const dir = dirOf(packageFile);
  const all = await directoryFiles(ctx.root).list();
  return all.filter((f) => basename(f) === 'packages.lock.json' && (dir === '.' || f.startsWith(`${dir}/`)));
}

async function restore(ctx: AdapterContext, items: PlanItem[], extra: string[]): Promise<{ outcome: StepOutcome; targets: string[] }> {
  const targets = new Set<string>();
  for (const i of items) {
    const kind = kindOf(i.packageFile);
    if (kind === 'project' || kind === 'props') for (const t of await restoreTargets(ctx, i.packageFile)) targets.add(t);
  }
  for (const t of targets) {
    const r = await ctx.exec('dotnet', ['restore', t, ...extra], { cwd: ctx.root, env: DOTNET_ENV, timeoutMs: 20 * 60_000 });
    if (r.code !== 0) return { outcome: outcome(r), targets: [...targets] };
  }
  return { outcome: OK, targets: [...targets] };
}

export const nugetAdapter: Adapter = {
  id: 'nuget',
  matches: (item) => item.datasource === 'nuget' && kindOf(item.packageFile) !== null,
  async files(ctx, item) {
    return [item.packageFile, ...item.lockFiles, ...(await lockFilesFor(ctx, item.packageFile))];
  },
  async edit(ctx, item) {
    const doc = await readDoc(ctx, item.packageFile);
    const kind = kindOf(item.packageFile);
    if (kind === 'tools') editDotnetTools(doc, item.packageFile, item);
    else if (kind === 'global.json') editGlobalJsonSdk(doc, item.packageFile, item);
    else editMsBuild(doc, item.packageFile, item);
    await writeDoc(ctx, item.packageFile, doc);
  },
  async relock(ctx, items) {
    // S13 csproj/cpm-textedit-restore: `dotnet restore --force-evaluate` rewrites packages.lock.json.
    const r = await restore(ctx, items, ['--force-evaluate']);
    if (!r.outcome.ok) return r.outcome;
    for (const i of items.filter((x) => kindOf(x.packageFile) === 'tools')) {
      const t = await ctx.exec('dotnet', ['tool', 'restore'], { cwd: rel2abs(ctx, dirOf(dirOf(i.packageFile)) || '.'), env: DOTNET_ENV, timeoutMs: 10 * 60_000 });
      if (t.code !== 0) return outcome(t);
    }
    return OK;
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      for (const lock of await lockFilesFor(ctx, i.packageFile)) {
        const json = JSON.parse(await fs.readFile(rel2abs(ctx, lock), 'utf8')) as { dependencies?: Record<string, Record<string, { type?: string; resolved?: string }>> };
        for (const tfm of Object.values(json.dependencies ?? {})) {
          const entry = Object.entries(tfm).find(([k]) => k.toLowerCase() === i.depName.toLowerCase())?.[1];
          if (entry && (entry.type === 'Direct' || entry.type === 'CentralTransitive') && entry.resolved !== i.newVersion) {
            misses.push(`${i.depName} resolved ${entry.resolved} (planned ${i.newVersion}) in ${lock}`);
          }
        }
      }
    }
    return misses.length ? notPinned(misses) : OK;
  },
  async parseBlockers(output, ctx, items) {
    const out: Blocker[] = [];
    // Structured first: obj/project.assets.json logs[] of every restored project (S13).
    for (const i of items) {
      for (const target of await restoreTargets(ctx, i.packageFile).catch(() => [] as string[])) {
        if (!/proj$/i.test(target)) continue;
        const assets = path.join(rel2abs(ctx, dirOf(target)), 'obj', 'project.assets.json');
        const text = await fs.readFile(assets, 'utf8').catch(() => null);
        if (text) out.push(...parseNuGetAssets(text));
      }
    }
    if (out.length === 0) out.push(...parseNuGetText(output));
    const seen = new Set<string>();
    return out.filter((b) => {
      const k = `${b.depName}|${b.code}`;
      if (seen.has(k)) return false;
      seen.add(k);
      return true;
    });
  },
  consistencyDir: (item) => item.packageFile,
  async consistency(ctx, packageFile) {
    if ((await lockFilesFor(ctx, packageFile)).length === 0) return OK;
    return (await restore(ctx, [{ packageFile } as PlanItem], ['--locked-mode'])).outcome;
  },
  tools: () => ['dotnet'],
};
