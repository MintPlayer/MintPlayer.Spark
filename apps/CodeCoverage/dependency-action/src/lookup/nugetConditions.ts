import type { LookupRecord } from '../contracts/types';
import { parseText } from '../core/lineEdit';
import { conditionOf, conditionTfms, ownTfms, scanItems, type MsBuildItem } from '../core/msbuildItems';
import { basename, dirname, joinRel, type RepoFiles } from '../util/repoFiles';

/**
 * multi-target-dependency-split-PRD D-a (S1): Renovate reports one dep per PackageReference/PackageVersion, ignores its
 * Condition, gives no position, and returns them out of document order. So the lookup reads each MSBuild file itself and
 * joins Renovate's deps to the file's items by (depName, currentValue), in document order. Identical duplicates get
 * identical candidates, so the join is lossless. A record under a condition gets `condition`, and `targetFrameworks` when
 * the condition maps to TFMs of its project (S3); one that maps to none keeps only `condition` (D-f).
 */

const PROJECT = /\.(cs|fs|vb)proj$/i;
const MSBUILD = /\.(cs|fs|vb)proj$|\.props$|\.targets$/i;

/** The TFMs a file's items build for: a project's own list (else its nearest Directory.Build.props's), and for a shared
 * props file every project under its folder, together. */
export async function fileTfms(files: RepoFiles, file: string, all?: string[]): Promise<string[]> {
  const list = all ?? (await files.list());
  if (PROJECT.test(file)) return projectTfms(files, file, list);
  const dir = dirname(file);
  const out = new Set<string>();
  for (const p of list.filter((f) => PROJECT.test(f) && (dir === '.' || f.startsWith(`${dir}/`)))) {
    for (const t of await projectTfms(files, p, list)) out.add(t);
  }
  return [...out];
}

async function projectTfms(files: RepoFiles, project: string, all: string[]): Promise<string[]> {
  const own = ownTfms(await files.read(project));
  if (own) return own;
  for (let dir = dirname(project); ; dir = dirname(dir)) {
    const props = joinRel(dir, 'Directory.Build.props');
    if (all.includes(props)) {
      const inherited = ownTfms(await files.read(props));
      if (inherited) return inherited;
    }
    if (dir === '.') return [];
  }
}

export async function annotateConditions(records: LookupRecord[], files: RepoFiles): Promise<void> {
  const byFile = new Map<string, LookupRecord[]>();
  for (const r of records) {
    if (r.datasource !== 'nuget' || !MSBUILD.test(basename(r.packageFile)) || r.currentValue === null) continue;
    byFile.set(r.packageFile, [...(byFile.get(r.packageFile) ?? []), r]);
  }
  if (byFile.size === 0) return;
  const all = await files.list();
  for (const [file, list] of byFile) {
    if (!all.includes(file)) continue;
    const items = scanItems(parseText(await files.readBytes(file), file));
    if (!items.some((i) => conditionOf(i) !== null)) continue;
    const tfms = await fileTfms(files, file, all);
    const taken = new Set<MsBuildItem>();
    for (const record of list) {
      const item = items.find((i) => !taken.has(i) && i.include.toLowerCase() === record.depName.toLowerCase() && i.version === record.currentValue);
      if (!item) continue;
      taken.add(item);
      const condition = conditionOf(item);
      if (condition === null) continue;
      record.condition = condition;
      const mapped = conditionTfms(condition, tfms);
      if (mapped) record.targetFrameworks = mapped;
    }
  }
}
