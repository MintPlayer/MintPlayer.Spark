import type { PlatformCandidate, PlatformRecord, PlatformTable, Sdk } from '../contracts/types';
import { CONTRACT_VERSION, SDKS } from '../contracts/types';
import { compareChannels, isPreviewVersion, sortChannelsDesc } from './channels';
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
  const { sdks, unstable } = (parsed ?? {}) as Record<string, unknown>;
  if (typeof sdks !== 'object' || sdks === null || Array.isArray(sdks)) throw new Error('input platforms.sdks must be an object of channel lists');
  if (!Array.isArray(unstable) || unstable.some((g) => typeof g !== 'string')) throw new Error('input platforms.unstable must be an array of package globs');
  const table: PlatformTable = { sdks: {}, unstable: unstable as string[] };
  for (const [sdk, list] of Object.entries(sdks)) {
    if (!(SDKS as readonly string[]).includes(sdk)) continue; // an SDK this action has no adapter for yet
    if (!Array.isArray(list)) throw new Error(`input platforms.sdks.${sdk} must be an array`);
    table.sdks[sdk as Sdk] = list.map((c, i) => candidate(c, `platforms.sdks.${sdk}[${i}]`));
  }
  return table;
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
 */
export function buildPlatformRecord(sdk: Sdk, found: Found[], feed: PlatformCandidate[] | null, now = new Date()): PlatformRecord {
  const editable = found.filter((f) => !f.computed && f.channel !== null && f.kind !== 'package-version');
  const channels = sortChannelsDesc(editable.map((f) => f.channel!));
  const highest = channels[0];
  const preview = editable.some((f) => isPreviewVersion(f.value));
  const candidates = (feed ?? [])
    .filter((c) => !highest || compareChannels(c.channel, highest) > 0)
    .filter((c) => !isEol(c.eol, now))
    .sort((a, b) => compareChannels(b.channel, a.channel));
  return {
    contractVersion: CONTRACT_VERSION,
    kind: 'platform',
    sdk,
    source: feed === null ? 'none' : sdk === 'dotnet' ? 'releases-index' : 'endoflife.date',
    current: { channels, preview },
    locations: found.map(toLocation),
    candidates,
  };
}
