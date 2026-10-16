import type { Sdk } from '../contracts/types';

/**
 * A platform channel is its major line as the platform itself names it: dotnet '10.0', node '24',
 * python '3.13', java '21', go '1.25'. The platform adapters only ever compare and substitute these
 * tokens; full versions come from the official feed (D-S12c), never from arithmetic.
 */
export function channelOf(sdk: Sdk, version: string): string | null {
  const v = version.trim().replace(/^(v|go|temurin-|openjdk-|zulu-|corretto-|adoptopenjdk-)/i, '');
  let m: RegExpMatchArray | null;
  switch (sdk) {
    case 'dotnet':
      m = v.match(/^(\d+)\.(\d+)/);
      return m ? `${m[1]}.${m[2]}` : null;
    case 'node':
      m = v.match(/^(\d+)(?=$|[.\-x*])/);
      return m ? m[1] : null;
    case 'python':
    case 'go':
      m = v.match(/^(\d+)\.(\d+)(?=$|[.\-x*a-z])/);
      return m ? `${m[1]}.${m[2]}` : null;
    case 'java':
      m = v.match(/^1\.(\d+)(?=$|[._\-])/) ?? v.match(/^(\d+)(?=$|[.+_\-])/);
      return m ? m[1] : null;
  }
}

export function compareChannels(a: string, b: string): number {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

export function sortChannelsDesc(channels: Iterable<string>): string[] {
  return [...new Set(channels)].sort((a, b) => compareChannels(b, a));
}

/**
 * Rewrites one version-ish value from channel `from` to channel `to`, keeping its shape:
 *   '20' -> '22', '20.x' -> '22.x', 'v20' -> 'v22', '10.0.x' -> '11.0.x', '3.12-slim' -> '3.13-slim'.
 * A value pinned finer than the channel ('20.11.0', '10.0.1-alpine') takes `toVersion` (the feed's
 * latest of the new channel) and keeps its suffix. Returns null when the value is not on `from` or
 * cannot be rewritten without guessing (the caller then reports it, R8).
 */
export function rewriteVersionValue(sdk: Sdk, value: string, from: string, to: string, toVersion?: string | null): string | null {
  const m = value.match(/^([^\d]*)(\d+(?:\.\d+)*)(.*)$/s);
  if (!m) return null;
  const [, prefix, numeric, rest] = m;
  if (channelOf(sdk, numeric) !== from) return null;
  const fromSegs = from.split('.').length;
  const segs = numeric.split('.').length;
  if (segs <= fromSegs) return `${prefix}${to}${rest}`;
  if (!toVersion) return null;
  const target = toVersion.replace(/^(v|go)/i, '');
  if (channelOf(sdk, target) !== to) return null;
  // A prerelease toVersion ('11.0.0-rc.2') carries its own suffix; keep the value's suffix after it.
  return `${prefix}${target}${rest}`;
}

/** 'net10.0' -> 'net11.0' (keeps '-windows' etc.); null when the token is not on `from`. */
export function rewriteTfm(tfm: string, from: string, to: string): string | null {
  const m = tfm.match(/^net(\d+\.\d+)(-.*)?$/);
  if (!m || m[1] !== from) return null;
  return `net${to}${m[2] ?? ''}`;
}

export function tfmChannel(tfm: string): string | null {
  const m = tfm.trim().match(/^net(\d+\.\d+)(-.*)?$/);
  if (!m) return null;
  return Number(m[1].split('.')[0]) >= 5 ? m[1] : null;
}

export function isPreviewVersion(v: string): boolean {
  return /-(preview|rc|alpha|beta)/i.test(v);
}
