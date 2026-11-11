import type { Sdk } from '../contracts/types';

/**
 * A platform channel is its major line as the platform itself names it: dotnet '10.0', node '24',
 * python '3.13', java '21', go '1.25'. The platform adapters only ever compare and substitute these
 * tokens; full versions come from the official feed (D-S12c), never from arithmetic.
 */
export function channelOf(sdk: Sdk, version: string): string | null {
  const v = version.trim().replace(/^(v|go|temurin-|openjdk-|zulu-|corretto-|adoptopenjdk-|ruby-)/i, '');
  let m: RegExpMatchArray | null;
  switch (sdk) {
    case 'dotnet':
      m = v.match(/^(\d+)\.(\d+)/);
      return m ? `${m[1]}.${m[2]}` : null;
    case 'node':
      m = v.match(/^(\d+)(?=$|[.\-x*])/);
      return m ? m[1] : null;
    // #606 D10/D11: a release line per minor. PHP '8.4' (8.4.14, 8.4-fpm, 8.6.0RC5), Ruby '3.4' (3.4.7,
    // 4.1.0-preview1), Rust '1.99' (1.99.0, 1.100.0-beta.13).
    case 'python':
    case 'go':
    case 'php':
    case 'ruby':
    case 'rust':
      m = v.match(/^(\d+)\.(\d+)(?=$|[.\-x*a-zA-Z])/);
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
  // A java build ('temurin-17.0.8+7' -> '25.0.4.1+1') is part of the version: the old one goes.
  const kept = target.includes('+') ? rest.replace(/^\+[^\s\-_]*/, '') : rest;
  return `${prefix}${target}${kept}`;
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

/**
 * #617 D29: whether `next` is a later release than `current` on the same channel, for the in-channel move the table
 * offers. The leading version numbers decide first; with equal numbers only a preview leaving for a release, or an
 * earlier preview for a later one, is newer. A shape suffix (`-alpine`, `-slim`, a Java `+7` build on equal numbers) is
 * never a reason to move, so a table older than the repository can never move a pin backwards.
 */
export function isNewerVersion(next: string, current: string): boolean {
  const parse = (v: string) => {
    const m = v.trim().match(/^[^\d]*(\d+(?:\.\d+)*)(.*)$/s);
    return m ? { numbers: m[1].split('.').map(Number), rest: m[2] } : null;
  };
  const a = parse(next);
  const b = parse(current);
  if (!a || !b) return false;
  for (let i = 0; i < Math.max(a.numbers.length, b.numbers.length); i++) {
    const d = (a.numbers[i] ?? 0) - (b.numbers[i] ?? 0);
    if (d !== 0) return d > 0;
  }
  const aPreview = isPreviewVersion(next);
  const bPreview = isPreviewVersion(current);
  if (!bPreview) return false;
  if (!aPreview) return true;
  // Both previews of the same version: compare their identifiers (rc.2.26504.105 < rc.3.26522.148).
  const ids = (rest: string) => rest.replace(/^[-.]/, '').split(/[.-]/);
  const [x, y] = [ids(a.rest), ids(b.rest)];
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    if (x[i] === undefined) return false;
    if (y[i] === undefined) return true;
    const [nx, ny] = [Number(x[i]), Number(y[i])];
    const d = Number.isNaN(nx) || Number.isNaN(ny) ? x[i].localeCompare(y[i]) : nx - ny;
    if (d !== 0) return d > 0;
  }
  return false;
}

export function isPreviewVersion(v: string): boolean {
  // PHP spells its candidates without a dash (8.6.0RC5); Python too (3.16.0a1, 3.15.0rc2).
  return /-(preview|rc|alpha|beta)/i.test(v) || /^\d+\.\d+\.\d+(RC|alpha|beta|a|b|rc)\d+$/.test(v);
}
