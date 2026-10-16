import { promises as fs } from 'node:fs';
import type { Blocker, PlanItem } from '../../contracts/types';
import { escapeRegExp } from '../../core/lineEdit';
import { basename } from '../../util/repoFiles';
import { OK, dirOf, editFile, exists, notPinned, outcome, rel2abs, tokenOnNamedLine, type Adapter } from '../adapter';

/**
 * Bundler (S12 reports rubygems; R12: every reported ecosystem gets an adapter). NOT spiked in S13/S14:
 * a Gemfile is Ruby, so `bundle lock` evaluates repository code like Gradle does. Treat the job as
 * executing repo code: never feed secrets. The parser's message shapes are Bundler's documented ones,
 * not a captured fixture (unverified until a capture lands).
 */
function lockOf(item: PlanItem): string {
  return item.lockFiles.find((l) => basename(l) === 'Gemfile.lock') ?? `${item.packageFile}.lock`;
}

export function parseBundler(output: string): Blocker[] {
  const out: Blocker[] = [];
  const missing = output.match(/Could not find gem '(\S+) \(([^)]+)\)'/);
  if (missing) out.push({ depName: missing[1], version: missing[2], code: 'bundler-missing-gem', message: missing[0] });
  const conflict = output.match(/(?:could not find compatible versions for gem|Could not find compatible versions)[^\n]*?"?([\w.-]+)?"?:?\s*\n([\s\S]{0,1200})/);
  if (!missing && conflict) {
    const because = conflict[2].match(/Because (\S+) (?:>=|=|<) ?(\S+) depends on/);
    out.push({ depName: conflict[1] ?? because?.[1] ?? null, version: because?.[2] ?? null, code: 'bundler-conflict', message: conflict[0].slice(0, 1500) });
  }
  return out;
}

export const bundlerAdapter: Adapter = {
  id: 'bundler',
  matches: (item) => item.datasource === 'rubygems' && /(^|\/)(Gemfile|gems\.rb)$/.test(item.packageFile),
  files: async (_ctx, item) => [item.packageFile, lockOf(item)],
  edit: (ctx, item) => editFile(ctx, item.packageFile, tokenOnNamedLine(new RegExp(`^\\s*gem\\s+['"]${escapeRegExp(item.depName)}['"]`), item.currentValue, item.newValue)),
  async relock(ctx, items) {
    const lock = lockOf(items[0]);
    if (!(await exists(ctx, lock))) return OK;
    const r = await ctx.exec('bundle', ['lock', '--update', ...items.map((i) => i.depName), '--conservative'], {
      cwd: rel2abs(ctx, dirOf(items[0].packageFile)),
      env: { BUNDLE_FROZEN: 'false', BUNDLE_DEPLOYMENT: 'false', BUNDLE_GEMFILE: basename(items[0].packageFile) },
      timeoutMs: 15 * 60_000,
    });
    return outcome(r);
  },
  async verify(ctx, items) {
    const misses: string[] = [];
    for (const i of items) {
      const text = await fs.readFile(rel2abs(ctx, lockOf(i)), 'utf8').catch(() => null);
      if (text !== null && !new RegExp(`^    ${escapeRegExp(i.depName)} \\(${escapeRegExp(i.newVersion)}\\)`, 'm').test(text)) misses.push(`${i.depName} ${i.newVersion}`);
    }
    return misses.length ? notPinned(misses) : OK;
  },
  parseBlockers: (output) => parseBundler(output),
  tools: (item) => (item.lockFiles.length ? ['bundle'] : []),
};
