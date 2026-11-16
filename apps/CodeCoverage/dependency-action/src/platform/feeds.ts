import type { FollowRule, PlatformCandidate, PlatformRecord, PlatformTable, Sdk } from '../contracts/types';
import { CONTRACT_VERSION, SDKS } from '../contracts/types';
import { compareChannels, isPreviewVersion, sortChannelsDesc } from './channels';
import { inChannelMoveChanges } from './applyPlatform';
import { toLocation, type Found } from './locations';

/**
 * #606 D9: the channel table is the bot's, passed as the lookup's required `platforms` input. The action fetches no
 * feed: the bot collects Microsoft's releases-index.json (.NET) and endoflife.date (every other SDK) once a day, plus
 * each SDK's preview feed, and every repository is offered moves from the same facts.
 */
export function parsePlatformTable(raw: string): PlatformTable {
  if (raw.trim() === '') {
    throw new Error('input platforms is required for mode lookup: the bot passes its channel table (update the dependency-lookup workflow to the current template)');
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new Error('input platforms must be the JSON channel table {"sdks":{...},"unstable":[...]}');
  }
  const { sdks, unstable, stable = [], follow = [] } = (parsed ?? {}) as Record<string, unknown>;
  if (typeof sdks !== 'object' || sdks === null || Array.isArray(sdks)) throw new Error('input platforms.sdks must be an object of channel lists');
  if (!Array.isArray(unstable) || unstable.some((g) => typeof g !== 'string')) throw new Error('input platforms.unstable must be an array of package globs');
  // prerelease-dependencies-PRD H5: absent while the bot that dispatches this predates it (the action's moving tag and the
  // server deploy separately).
  if (!Array.isArray(stable) || stable.some((g) => typeof g !== 'string')) throw new Error('input platforms.stable must be an array of package globs');
  // dependency-companion-pins-PRD F2: absent while the bot that dispatches this predates it.
  if (!Array.isArray(follow)) throw new Error('input platforms.follow must be an array of follow rules');
  const table: PlatformTable = { sdks: {}, unstable: unstable as string[], stable: stable as string[], follow: follow.map((r, i) => followRule(r, `platforms.follow[${i}]`)) };
  for (const [sdk, list] of Object.entries(sdks)) {
    if (!(SDKS as readonly string[]).includes(sdk)) continue; // an SDK this action has no adapter for yet
    if (!Array.isArray(list)) throw new Error(`input platforms.sdks.${sdk} must be an array`);
    table.sdks[sdk as Sdk] = list.map((c, i) => candidate(c, `platforms.sdks.${sdk}[${i}]`));
  }
  return table;
}

function followRule(value: unknown, at: string): FollowRule {
  const r = (value ?? {}) as Record<string, unknown>;
  for (const key of ['dependency', 'path', 'match']) {
    if (typeof r[key] !== 'string' || r[key] === '') throw new Error(`input ${at}.${key} must be a non-empty string`);
  }
  if (r.version !== undefined && r.version !== null && typeof r.version !== 'string') throw new Error(`input ${at}.version must be a string`);
  return { dependency: r.dependency as string, path: r.path as string, match: r.match as string, version: (r.version as string | null | undefined) ?? null };
}

function candidate(value: unknown, at: string): PlatformCandidate {
  const c = (value ?? {}) as Record<string, unknown>;
  const str = (k: string) => (typeof c[k] === 'string' ? (c[k] as string) : null);
  if (typeof c.channel !== 'string' || !/^\d{1,4}(\.\d{1,4})?$/.test(c.channel)) throw new Error(`input ${at}.channel must be a channel such as 10.0 or 24`);
  const eol = c.eol;
  const latestVersion = str('latestVersion');
  return {
    channel: c.channel,
    latestVersion,
    latestSdk: str('latestSdk'),
    releaseTimestamp: str('releaseTimestamp'),
    preview: c.preview === true || (latestVersion !== null && isPreviewVersion(latestVersion)),
    eol: typeof eol === 'string' || typeof eol === 'boolean' ? eol : null,
    lts: typeof c.lts === 'boolean' ? c.lts : null,
  };
}

function isEol(eol: string | boolean | null, now: Date): boolean {
  if (eol === true) return true;
  if (typeof eol === 'string') return new Date(eol).getTime() < now.getTime();
  return false;
}

/**
 * One platform record per sdk that has at least one location. Candidates are the channels newer than
 * the highest current one and not end-of-life; previews are kept and flagged: the bot decides per the
 * SDK's track (#606 D8) which one it offers.
 *
 * #617 D29: the highest current channel itself is a candidate too (last, after every newer one) when a move within it
 * changes something: a pin below the channel's latest version or SDK (11.0.100-rc.2 → 11.0.100, 20.11.0 → 20.20.2), or
 * a `dotnet-quality` to drop once the channel's latest is a release. The table is then the one source of every SDK move,
 * and R31 folds Renovate's in-channel candidates of the same locations away.
 */
export function buildPlatformRecord(sdk: Sdk, found: Found[], feed: PlatformCandidate[] | null, now = new Date()): PlatformRecord {
  const editable = found.filter((f) => !f.computed && f.channel !== null && f.kind !== 'package-version' && f.kind !== 'dotnet-quality');
  const channels = sortChannelsDesc(editable.map((f) => f.channel!));
  const highest = channels[0];
  const preview = editable.some((f) => isPreviewVersion(f.value));
  const newer = (feed ?? [])
    .filter((c) => !highest || compareChannels(c.channel, highest) > 0)
    .filter((c) => !isEol(c.eol, now))
    .sort((a, b) => compareChannels(b.channel, a.channel));
  const same = highest ? (feed ?? []).find((c) => c.channel === highest) : undefined;
  const inChannel = same && inChannelMoveChanges(found, { sdk, fromChannel: highest, toChannel: highest, toVersion: same.latestVersion, sdkVersion: same.latestSdk ?? null });
  const candidates = inChannel ? [...newer, same] : newer;
  // multi-target-dependency-split-PRD M6: a multi-target project drops a TFM once its channel is end of life, and the
  // candidates above hold only newer channels, so the current ones that are past it are named here.
  const endOfLife = channels.filter((ch) => (feed ?? []).some((c) => c.channel === ch && isEol(c.eol, now)));
  return {
    contractVersion: CONTRACT_VERSION,
    kind: 'platform',
    sdk,
    source: feed === null ? 'none' : sdk === 'dotnet' ? 'releases-index' : 'endoflife.date',
    current: endOfLife.length > 0 ? { channels, preview, endOfLife } : { channels, preview },
    locations: found.map(toLocation),
    candidates,
  };
}
