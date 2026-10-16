import { promises as fs } from 'node:fs';
import type { PlanItem } from '../../contracts/types';
import { EditRefused, escapeRegExp, parseText, serializeText } from '../../core/lineEdit';
import { basename } from '../../util/repoFiles';
import { OK, rel2abs, tokenOnNamedLine, editFile, tokenRegExp, type Adapter, type AdapterContext } from '../adapter';

/**
 * Text-only ecosystems (S13: "no native tool"): Docker FROM/image tags, Actions `uses:` (SHA + `# vX`),
 * runner labels and wrapper URLs. Renovate gives the exact `replaceString`; the new string is that
 * string with the current value and digest tokens swapped. Several identical occurrences in one file
 * (two stages FROM the same image) are applied together only when the group plans every one of them.
 */

const DATASOURCES = new Set(['docker', 'github-tags', 'github-releases', 'github-digest', 'github-runners']);
const MANAGERS = new Set(['maven-wrapper', 'gradle-wrapper', 'dockerfile', 'docker-compose', 'github-actions', 'kubernetes', 'helm-values']);

export type DigestResolver = (item: PlanItem) => Promise<string | null>;

/** New replace string, or a refusal reason. */
export function nextReplaceString(item: PlanItem, newDigest: string | null): string | { refuse: string } {
  const rs = item.replaceString!;
  let next = rs;
  if (item.currentDigest) {
    if (!newDigest) return { refuse: `${item.depName} is digest-pinned and the plan has no newDigest` };
    if (!next.includes(item.currentDigest)) return { refuse: `replaceString does not contain the current digest` };
    next = next.split(item.currentDigest).join(newDigest);
  }
  const re = new RegExp(tokenRegExp(item.currentValue).source, 'g');
  if (!re.test(next)) {
    // A digest-only update keeps the value (Go pseudo-versions, docker `digest` updates).
    if (item.currentValue === item.newValue && item.currentDigest) return next;
    return { refuse: `replaceString '${rs}' does not contain '${item.currentValue}'` };
  }
  return next.replace(new RegExp(tokenRegExp(item.currentValue).source, 'g'), item.newValue);
}

function count(haystack: string, needle: string): number {
  let n = 0;
  let i = 0;
  while ((i = haystack.indexOf(needle, i)) >= 0) {
    n++;
    i += needle.length;
  }
  return n;
}

/** Actions: the commit of a tag (peeled for annotated tags), via `git ls-remote` (S13). */
export function lsRemoteResolver(ctx: AdapterContext): DigestResolver {
  return async (item) => {
    if (item.datasource === 'docker') {
      const r = await ctx.exec('docker', ['buildx', 'imagetools', 'inspect', `${item.depName}:${item.newValue}`, '--format', '{{json .Manifest.Digest}}'], { cwd: ctx.root, timeoutMs: 120_000 });
      const m = r.stdout.match(/"(sha256:[0-9a-f]{64})"/);
      return r.code === 0 && m ? m[1] : null;
    }
    const [owner, repo] = item.depName.split('/');
    if (!owner || !repo) return null;
    const tag = item.newValue;
    const r = await ctx.exec('git', ['ls-remote', `https://github.com/${owner}/${repo}`, `refs/tags/${tag}`, `refs/tags/${tag}^{}`], { cwd: ctx.root, timeoutMs: 120_000 });
    if (r.code !== 0) return null;
    const lines = r.stdout.trim().split('\n').map((l) => l.split('\t'));
    const peeled = lines.find(([, ref]) => ref?.endsWith('^{}'));
    const plain = lines.find(([, ref]) => ref === `refs/tags/${tag}`);
    const sha = (peeled ?? plain)?.[0];
    return sha && /^[0-9a-f]{40}$/.test(sha) ? sha : null;
  };
}

export function makeReplaceStringAdapter(resolver?: (ctx: AdapterContext) => DigestResolver): Adapter {
  return {
    id: 'replace-string',
    matches: (item) => !!item.replaceString && (DATASOURCES.has(item.datasource) || MANAGERS.has(item.manager)),
    files: async (_ctx, item) => [item.packageFile],
    async edit(ctx, item) {
      const key = `${item.packageFile}\0${item.replaceString}\0${item.newValue}`;
      if (ctx.memo.has(key)) return;
      ctx.memo.add(key);
      if (item.manager === 'gradle-wrapper') {
        const text = await fs.readFile(rel2abs(ctx, item.packageFile), 'utf8');
        if (/^distributionSha256Sum=/m.test(text)) throw new EditRefused(item.packageFile, 'gradle-wrapper.properties pins distributionSha256Sum; the checksum cannot be planned', 0);
      }
      let digest = item.newDigest ?? null;
      if (item.currentDigest && !digest) digest = await (resolver ?? lsRemoteResolver)(ctx)(item);
      const next = nextReplaceString(item, digest);
      if (typeof next !== 'string') throw new EditRefused(item.packageFile, next.refuse, 0);
      const bytes = await fs.readFile(rel2abs(ctx, item.packageFile));
      const doc = parseText(bytes, item.packageFile);
      const siblings = ctx.group.filter((g) => g.packageFile === item.packageFile && g.replaceString === item.replaceString && g.newValue === item.newValue).length || 1;
      const occurrences = doc.lines.reduce((n, l) => n + count(l.text, item.replaceString!), 0);
      if (occurrences !== siblings) {
        throw new EditRefused(item.packageFile, `'${item.replaceString}' occurs ${occurrences}x, the group plans ${siblings}`, occurrences);
      }
      for (const l of doc.lines) l.text = l.text.split(item.replaceString!).join(next);
      await fs.writeFile(rel2abs(ctx, item.packageFile), serializeText(doc));
    },
    parseBlockers: () => [],
    tools: () => [],
  };
}

export const replaceStringAdapter = makeReplaceStringAdapter();

// ---------------------------------------------------------------- version files (as plain items)

const WITH_KEYS: Record<string, string> = {
  node: 'node-version',
  python: 'python-version',
  go: 'go-version',
  'java-jdk': 'java-version',
  java: 'java-version',
  'dotnet-sdk': 'dotnet-version',
};

const VERSION_DATASOURCES = new Set(['node-version', 'python-version', 'java-version', 'dotnet-version', 'golang-version', 'github-releases', 'github-tags', 'docker']);
const VERSION_MANAGERS = new Set(['nvm', 'nodenv', 'pyenv', 'asdf', 'github-actions', 'nuget', 'gomod']);

/**
 * A platform version planned as a plain item (a patch/minor inside the channel, e.g. .nvmrc 20.11.0 ->
 * 20.20.2, setup-node `node-version:`, global.json sdk, `go 1.21.5`). Channel moves go through a
 * platform group (R7) instead. Never touches anything but the one token.
 */
export const versionFileAdapter: Adapter = {
  id: 'version-file',
  matches: (item) => !item.replaceString && VERSION_DATASOURCES.has(item.datasource) && VERSION_MANAGERS.has(item.manager) && !item.depName.includes('/'),
  files: async (_ctx, item) => [item.packageFile],
  async edit(ctx, item) {
    const file = item.packageFile;
    const name = basename(file);
    let filter: RegExp;
    if (/^\.github\/|action\.ya?ml$/.test(file)) {
      const key = WITH_KEYS[item.depName];
      if (!key) throw new EditRefused(file, `no setup-* input for ${item.depName}`, 0);
      filter = new RegExp(`^\\s*${escapeRegExp(key)}:`);
    } else if (name === 'global.json') filter = /"version"\s*:/;
    else if (name === '.tool-versions') filter = new RegExp(`^\\s*${escapeRegExp(item.depName)}\\s`);
    else if (name === 'go.mod') filter = /^(go|toolchain)\s/;
    else filter = /./;
    await editFile(ctx, file, tokenOnNamedLine(filter, item.currentValue, item.newValue));
  },
  verify: async () => OK,
  parseBlockers: () => [],
  tools: () => [],
};
