import type { Candidate, FollowRule, LookupDocument, LookupRecord, UpdateType } from '../contracts/types';
import { globToRegExp, type RepoFiles } from '../util/repoFiles';

export type { FollowRule };

// The rules come from the bot's `platforms` table; the server validated them (DependenciesYml): `match` and `version`
// have exactly one `version` group.

/** The server's `DependencyFollowRule.FirstVersion`: a Docker variant (`-noble`, `-alpine3.20`) is not a prerelease. */
const FIRST_VERSION = /[0-9]+(\.[0-9]+)+(-(alpha|beta|rc|preview|pre|next|canary|dev)(\.?[0-9A-Za-z]+)*)?/i;

/** The paths no dependency patch may touch (the server's DependencyPatchValidator refuses them first). */
const DENIED = /^(\.github\/|\.mintplayer\/|coverage\.ya?ml$)/;

/** The version a follower is written with for the leader's `leaderValue`, or null when it has none. */
export function followerVersion(rule: FollowRule, leaderValue: string): string | null {
  if (rule.version) {
    const group = new RegExp(rule.version).exec(leaderValue)?.groups?.version;
    return group ? group : null;
  }
  return FIRST_VERSION.exec(leaderValue)?.[0] ?? null;
}

/**
 * One record per match of every rule, as a record of the leader (manager `follow`), so the follower is in the leader's
 * group and moves to the version the leader moves to. A rule that cannot be followed is a problem of the lookup, which
 * the pull request body shows as "not moved" (G4).
 */
export async function followerRecords(files: RepoFiles, rules: readonly FollowRule[], records: readonly LookupRecord[], problems: LookupDocument['problems']): Promise<LookupRecord[]> {
  const out: LookupRecord[] = [];
  const all = rules.length ? await files.list() : [];
  for (const [index, rule] of rules.entries()) {
    const source = `follow[${index}]`;
    const problem = (message: string) => problems.push({ source, message: `${rule.dependency} in ${rule.path}: ${message}` });
    if (DENIED.test(rule.path)) {
      problem('a dependency update never edits .github/, .mintplayer/ or coverage.yml; this pin is not moved');
      continue;
    }
    let match: RegExp;
    try {
      match = new RegExp(rule.match, 'g');
      if (rule.version) new RegExp(rule.version);
    } catch (e) {
      problem(`the pattern is not a JavaScript regular expression (${(e as Error).message}); this pin is not moved`);
      continue;
    }
    const leaders = records.filter((r) => r.depName === rule.dependency && r.manager !== 'follow');
    if (leaders.length === 0) {
      problem(`${rule.dependency} is not a dependency the lookup reported; this pin is not moved`);
      continue;
    }
    const glob = globToRegExp(rule.path);
    const found: LookupRecord[] = [];
    for (const file of all.filter((f) => glob.test(f) && !DENIED.test(f))) {
      const text = await files.read(file);
      for (const m of text.matchAll(match)) {
        const current = m.groups?.version;
        if (!current) continue;
        found.push(record(rule, leaders, file, m[0], current));
      }
    }
    if (found.length === 0) problem('the pattern matched nothing; this pin is not moved');
    out.push(...found);
  }
  return out;
}

function record(rule: FollowRule, leaders: readonly LookupRecord[], packageFile: string, replaceString: string, current: string): LookupRecord {
  const leader = leaders[0];
  const candidates = new Map<string, Candidate>();
  // The leader's current value too: a follower that lags its leader (a half-applied move) catches up.
  // follower-cooldown E2: `version` is the leader's token (the engine's `Token`: version first), so the follower shares
  // the leader's first-seen clock and own-release check; `value` is what is written into the follower's file.
  const offered: Array<{ value: string; token: string; from: Candidate | null }> = [
    ...leaders.map((l) => ({ value: l.currentValue ?? '', token: l.currentVersion ?? l.currentValue ?? '', from: null })),
    ...leaders.flatMap((l) => l.candidates.filter((c) => c.value !== null && c.updateType !== 'digest').map((c) => ({ value: c.value!, token: c.version ?? c.value!, from: c }))),
  ];
  for (const { value, token, from } of offered) {
    const version = followerVersion(rule, value);
    if (version === null || candidates.has(version) || compareVersions(version, current) <= 0) continue;
    candidates.set(version, {
      version: token,
      value: version,
      updateType: updateTypeOf(current, version),
      releaseTimestamp: from?.releaseTimestamp ?? null,
      digest: null,
      isBreaking: from?.isBreaking ?? false,
      isLockfileUpdate: false,
      groupName: from?.groupName ?? null,
      branchName: null,
      missing: from?.releaseTimestamp ? [] : ['releaseTimestamp'],
    });
  }
  const list = [...candidates.values()].sort((a, b) => compareVersions(a.value!, b.value!));
  return {
    ecosystem: leader.ecosystem,
    manager: 'follow',
    datasource: leader.datasource,
    packageFile,
    lockFiles: [],
    depName: rule.dependency,
    packageName: leader.packageName,
    depType: null,
    currentValue: current,
    currentVersion: current,
    lockedVersion: null,
    currentDigest: null,
    replaceString,
    sharedVariableName: null,
    groupName: leader.groupName,
    skipReason: null,
    hold: false,
    candidates: list,
  };
}

function parts(v: string): { nums: number[]; pre: string | null } {
  const [core, ...pre] = v.split('-');
  return { nums: core.split('.').map((n) => Number.parseInt(n, 10) || 0), pre: pre.length ? pre.join('-') : null };
}

/** Numeric parts first; a prerelease sorts below its release. */
export function compareVersions(a: string, b: string): number {
  const x = parts(a);
  const y = parts(b);
  for (let i = 0; i < Math.max(x.nums.length, y.nums.length); i++) {
    const d = (x.nums[i] ?? 0) - (y.nums[i] ?? 0);
    if (d !== 0) return Math.sign(d);
  }
  if (x.pre === y.pre) return 0;
  if (x.pre === null) return 1;
  if (y.pre === null) return -1;
  return x.pre.localeCompare(y.pre, 'en', { numeric: true });
}

function updateTypeOf(from: string, to: string): UpdateType {
  const x = parts(from).nums;
  const y = parts(to).nums;
  if ((x[0] ?? 0) !== (y[0] ?? 0)) return 'major';
  if ((x[1] ?? 0) !== (y[1] ?? 0)) return 'minor';
  return 'patch';
}
