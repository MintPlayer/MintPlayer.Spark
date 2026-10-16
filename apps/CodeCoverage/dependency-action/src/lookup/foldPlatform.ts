import type { LookupRecord, PlatformRecord, Sdk } from '../contracts/types';
import { notEditedReason } from '../platform/applyPlatform';
import { channelOf } from '../platform/channels';
import { platformOfImage, type Found } from '../platform/locations';

const SDKS = new Set<Sdk>(['dotnet', 'node', 'python', 'java', 'go']);

/** The sdk a Renovate record would be a platform location of: a platform ecosystem, or a platform image. */
function sdkOf(record: LookupRecord): Sdk | null {
  if (record.ecosystem.startsWith('platform:')) {
    const sdk = record.ecosystem.slice('platform:'.length) as Sdk;
    return SDKS.has(sdk) ? sdk : null;
  }
  if (record.datasource === 'docker') return platformOfImage(record.depName);
  return null;
}

/**
 * The platform location this record reports: same sdk and file, and the same token. Renovate's asdf
 * manager strips a distribution prefix (`java temurin-17.0.8+7` reports `17.0.8+7`), so a
 * .tool-versions value may end in the record's value.
 */
function locationOf(record: LookupRecord, sdk: Sdk, found: readonly Found[]): Found | null {
  const value = record.currentValue;
  if (value === null) return null;
  return (
    found.find(
      (f) =>
        f.sdk === sdk &&
        f.file === record.packageFile &&
        (f.value === value || (f.kind === 'tool-versions' && f.value.endsWith(`-${value}`))),
    ) ?? null
  );
}

export interface FoldResult {
  records: LookupRecord[];
  /** `file depName value` of each record folded away entirely, for the log. */
  folded: string[];
}

/**
 * R31 (S18-D1): the platform record owns its locations. A Renovate record that is a location the
 * platform adapter edits (a setup-* `with:` version, a platform image's tag, a version file,
 * .tool-versions, global.json, engines, go.mod) loses every candidate that moves it to another
 * channel: that move is the platform group's, so one approval moves every location to one target.
 * A record left without candidates is dropped; one with in-channel candidates (20.11.0 -> 20.20.2,
 * which the platform plan never does) stays with only those.
 *
 * Kept whole, because the platform plan would not move it:
 *  - a location the scanner does not recognise (a workflow `container:` image, `uses: docker://`);
 *  - a location the adapter only reports (computed, digest-pinned, a full-version mcr tag);
 *  - a location on a lower channel than the platform's current one (the plan's fromChannel is the
 *    highest, so the bot never moves it).
 */
export function foldPlatformLocations(records: readonly LookupRecord[], found: readonly Found[], platforms: readonly PlatformRecord[]): FoldResult {
  const fromChannel = new Map(platforms.map((p) => [p.sdk, p.current.channels[0]] as const));
  const out: LookupRecord[] = [];
  const folded: string[] = [];
  for (const record of records) {
    const sdk = sdkOf(record);
    const location = sdk ? locationOf(record, sdk, found) : null;
    if (!sdk || !location || notEditedReason(location) !== null || location.channel === null || location.channel !== fromChannel.get(sdk)) {
      out.push(record);
      continue;
    }
    const candidates = record.candidates.filter((c) => {
      const v = c.version ?? c.value;
      return v === null || channelOf(sdk, v) === location.channel;
    });
    if (candidates.length === 0) {
      // The location is already listed on the platform record, which now carries its only move.
      folded.push(`${record.packageFile} ${record.depName} ${record.currentValue}`);
      continue;
    }
    out.push({ ...record, candidates, hold: candidates.some((c) => c.updateType === null) });
  }
  return { records: out, folded };
}
