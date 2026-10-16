import type { PlatformCandidate, PlatformRecord, Sdk } from '../contracts/types';
import { CONTRACT_VERSION } from '../contracts/types';
import { channelOf, compareChannels, isPreviewVersion, sortChannelsDesc } from './channels';
import { toLocation, type Found } from './locations';

/** Official feeds (D-S12c). Fixtures of both are real captures (test/fixtures/platform-feeds). */
export const RELEASES_INDEX_URL = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json';
export const ENDOFLIFE_PRODUCT: Record<Exclude<Sdk, 'dotnet'>, string> = {
  node: 'nodejs',
  python: 'python',
  java: 'eclipse-temurin',
  go: 'go',
};

export interface ReleasesIndexEntry {
  'channel-version': string;
  'latest-release'?: string;
  'latest-release-date'?: string;
  'latest-sdk'?: string;
  'support-phase'?: string;
  'release-type'?: string;
  'eol-date'?: string | null;
}

export function candidatesFromReleasesIndex(json: { 'releases-index': ReleasesIndexEntry[] }): PlatformCandidate[] {
  return json['releases-index']
    .filter((e) => Number(e['channel-version'].split('.')[0]) >= 5)
    .map((e) => {
      const latest = e['latest-release'] ?? null;
      const phase = (e['support-phase'] ?? '').toLowerCase();
      return {
        channel: e['channel-version'],
        latestVersion: latest,
        latestSdk: e['latest-sdk'] ?? null,
        releaseTimestamp: e['latest-release-date'] ?? null,
        preview: phase === 'preview' || phase === 'go-live' || (latest !== null && isPreviewVersion(latest)),
        eol: phase === 'eol' ? true : (e['eol-date'] ?? null),
        lts: e['release-type'] ? e['release-type'] === 'lts' : null,
      };
    });
}

export interface EndOfLifeCycle {
  cycle: string;
  releaseDate?: string;
  eol?: string | boolean;
  latest?: string;
  latestReleaseDate?: string;
  lts?: string | boolean;
}

export function candidatesFromEndOfLife(sdk: Exclude<Sdk, 'dotnet'>, cycles: EndOfLifeCycle[]): PlatformCandidate[] {
  return cycles.map((c) => ({
    channel: channelOf(sdk, String(c.cycle)) ?? String(c.cycle),
    latestVersion: c.latest ?? null,
    releaseTimestamp: c.latestReleaseDate ?? c.releaseDate ?? null,
    preview: false,
    eol: c.eol ?? null,
    lts: c.lts === undefined ? null : c.lts !== false,
  }));
}

function isEol(eol: string | boolean | null, now: Date): boolean {
  if (eol === true) return true;
  if (typeof eol === 'string') return new Date(eol).getTime() < now.getTime();
  return false;
}

/**
 * One platform record per sdk that has at least one location. Candidates are the channels newer than
 * the highest current one and not end-of-life; previews are kept and flagged (R7: the bot follows a
 * preview only when the repo is already on one).
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

export type Fetcher = (url: string) => Promise<unknown>;

export const realFetcher: Fetcher = async (url) => {
  const res = await fetch(url, { headers: { accept: 'application/json', 'user-agent': 'mintplayer-dependency-update-action' } });
  if (!res.ok) throw new Error(`GET ${url}: HTTP ${res.status}`);
  return res.json();
};

export async function fetchCandidates(sdk: Sdk, fetcher: Fetcher): Promise<PlatformCandidate[]> {
  if (sdk === 'dotnet') {
    return candidatesFromReleasesIndex((await fetcher(RELEASES_INDEX_URL)) as { 'releases-index': ReleasesIndexEntry[] });
  }
  const product = ENDOFLIFE_PRODUCT[sdk];
  return candidatesFromEndOfLife(sdk, (await fetcher(`https://endoflife.date/api/${product}.json`)) as EndOfLifeCycle[]);
}
