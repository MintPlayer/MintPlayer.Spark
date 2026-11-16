import type { LookupRecord, PlatformRecord, PlatformTable, Sdk } from '../contracts/types';
import { SDKS as ALL_SDKS } from '../contracts/types';
import { followsWhenAuto, notEditedReason } from '../platform/applyPlatform';
import { channelOf, compareChannels } from '../platform/channels';
import { platformOfImage, type Found } from '../platform/locations';

const SDKS = new Set<Sdk>(ALL_SDKS);

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
 * A record left without candidates is dropped. A workflow's `container:`, `image:` and
 * `uses: docker://` images are docker locations too (#606 M6), so they fold like a Dockerfile's FROM.
 *
 * #617 D29/D34: the in-channel candidates (20.11.0 -> 20.20.2, 11.0.100-rc.2 -> 11.0.100) fold away too when the bot's
 * table has a row for the location's channel: the platform record then offers that move itself, to one target for every
 * location (Renovate offered `global.json` nothing beside its `setup-dotnet` siblings, #617 G1). Only a channel the table
 * has no row for keeps Renovate's in-channel candidates, so each location has exactly one source.
 *
 * Kept whole, because the platform plan would not move it:
 *  - a location the scanner does not recognise;
 *  - a location the adapter only reports (computed, digest-pinned, a full-version mcr tag);
 *  - a location on a lower channel that does not follow a channel move (a TFM, a matrix entry, one SDK of a
 *    multi-line setup list: P6, `followsWhenAuto`).
 * A lower-channel location that does follow keeps only its own channel's candidates.
 */
export function foldPlatformLocations(records: readonly LookupRecord[], found: readonly Found[], platforms: readonly PlatformRecord[], table?: PlatformTable): FoldResult {
  const fromChannel = new Map(platforms.map((p) => [p.sdk, p.current.channels[0]] as const));
  const tabled = (sdk: Sdk, channel: string) => table?.sdks[sdk]?.some((c) => c.channel === channel) === true;
  const out: LookupRecord[] = [];
  const folded: string[] = [];
  for (const record of records) {
    const sdk = sdkOf(record);
    const location = sdk ? locationOf(record, sdk, found) : null;
    const current = sdk ? fromChannel.get(sdk) : undefined;
    if (!sdk || !location || notEditedReason(location) !== null || location.channel === null || current === undefined) {
      out.push(record);
      continue;
    }
    // platform-locations-PRD P6: a location on a lower channel that follows a channel move (lagging `auto`) loses its
    // candidates to other channels, which are the platform group's move now; its own channel's patches stay Renovate's.
    // The lookup does not see `lagging`, so this is the `auto` split; under `report` the stage is listed as not moved.
    const lagging = location.channel !== current;
    if (lagging && (compareChannels(location.channel, current) > 0 || !followsWhenAuto(location))) {
      out.push(record);
      continue;
    }
    const candidates = !lagging && tabled(sdk, location.channel)
      ? []
      : record.candidates.filter((c) => {
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
