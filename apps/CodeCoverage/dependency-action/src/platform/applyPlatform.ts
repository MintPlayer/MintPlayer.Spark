import type { PlatformPlan } from '../contracts/types';
import { parseText, serializeText } from '../core/lineEdit';
import type { RepoFiles } from '../util/repoFiles';
import { compareChannels, isNewerVersion, isPreviewVersion, rewriteTfm, rewriteVersionValue, tfmChannel } from './channels';
import { scanLocations, type Found } from './locations';

export interface PlatformApplyResult {
  edited: string[];
  reported: Array<{ file: string; value: string; reason: string }>;
}

interface Replacement {
  line: number;
  start: number;
  end: number;
  text: string;
}

function globToRegExp(glob: string): RegExp {
  const src = glob
    .split('**')
    .map((part) => part.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('[^/]*'))
    .join('.*');
  return new RegExp(`^${src}$`);
}

function inScope(file: string, plan: PlatformPlan): boolean {
  if (plan.locations?.length && !plan.locations.some((g) => globToRegExp(g).test(file))) return false;
  if (plan.exclude?.some((g) => globToRegExp(g).test(file))) return false;
  return true;
}

/**
 * dotnet TFM list for `strategy`. `replace`: the from-channel token becomes the to-channel token.
 * `add`: the to-channel goes first, the newest `max(keepPrevious, 1)` existing modern channels stay,
 * and non-modern tokens (net48, netstandard2.0) always stay.
 */
export function nextTfmList(value: string, plan: PlatformPlan): string | null {
  const tokens = value.split(';').map((t) => t.trim()).filter(Boolean);
  if (!tokens.some((t) => tfmChannel(t) === plan.fromChannel)) return null;
  if ((plan.strategy ?? 'replace') === 'replace') {
    const next = tokens.map((t) => rewriteTfm(t, plan.fromChannel, plan.toChannel) ?? t);
    return [...new Set(next)].join(';');
  }
  const modern = tokens.filter((t) => tfmChannel(t) !== null);
  const legacy = tokens.filter((t) => tfmChannel(t) === null);
  const keep = Math.max(plan.keepPrevious ?? 0, 1);
  const kept = [...modern].sort((a, b) => compareChannels(tfmChannel(b)!, tfmChannel(a)!)).slice(0, keep);
  const fromToken = tokens.find((t) => tfmChannel(t) === plan.fromChannel)!;
  const added = rewriteTfm(fromToken, plan.fromChannel, plan.toChannel)!;
  return [...new Set([added, ...kept, ...legacy])].join(';');
}

/**
 * Why the platform adapter never edits this location, or null when a plan on the location's channel
 * does edit it. The lookup (R31) folds a Renovate record into the platform record only when this is
 * null, so the two sides agree on which locations the platform owns.
 */
export function notEditedReason(f: Found): string | null {
  if (f.computed) return f.reason ?? 'computed value';
  switch (f.kind) {
    case 'types-node':
    case 'requires-python':
    // #606: ranges and minimums are reported, never narrowed or raised (D11: the MSRV is never raised).
    case 'composer-require':
    case 'gemspec-ruby':
    case 'rust-version':
      return f.reason ?? 'reported only';
    case 'docker':
      return f.sdk === 'dotnet' && /^\d+\.\d+\.\d+/.test(f.value)
        ? `mcr tag '${f.value}' is pinned to a full version; it is updated as a docker record`
        : null;
    case 'tfm':
    case 'global.json':
    case 'package-version':
    case 'maven-release':
    case 'go.mod':
    case 'toolchain':
    case 'setup-action':
    case 'version-file':
    case 'tool-versions':
    case 'engines':
    case 'composer-platform':
    case 'gemfile-ruby':
    case 'rust-toolchain':
    case 'matrix':
    case 'dotnet-quality':
      return null;
    default:
      return 'unsupported location';
  }
}

/** #617 D31: the plan's target is a release (neither its SDK nor its version is a preview), so a quality goes. */
export function targetsRelease(plan: PlatformPlan): boolean {
  const target = plan.sdkVersion ?? plan.toVersion;
  return !!target && !isPreviewVersion(target) && !(plan.toVersion && isPreviewVersion(plan.toVersion));
}

/** #617 D31: a `dotnet-quality:` line this plan removes: a non-`ga` quality once the target is a release. */
function removesQuality(f: Found, plan: PlatformPlan): boolean {
  return f.kind === 'dotnet-quality' && notEditedReason(f) === null && f.value.toLowerCase() !== 'ga' && targetsRelease(plan);
}

/**
 * #617 D29: whether a move to `plan.toChannel` that stays on the same channel changes anything: a pin below the
 * channel's latest (never one above it: a table older than the repository moves nothing back), or a quality to drop.
 */
export function inChannelMoveChanges(found: readonly Found[], plan: PlatformPlan): boolean {
  return found.some((f) => {
    if (f.computed || (f.channel !== null && f.channel !== plan.fromChannel)) return false;
    if (f.kind === 'dotnet-quality') return removesQuality(f, plan);
    if (f.kind === 'tfm' || f.kind === 'package-version' || f.channel === null) return false;
    const { replacement } = replacementFor(f, plan);
    return replacement?.length === 1 && replacement[0].text !== f.value && isNewerVersion(replacement[0].text, f.value);
  });
}

function replacementFor(f: Found, plan: PlatformPlan): { replacement?: Replacement[]; report?: string } {
  const notEdited = notEditedReason(f);
  if (notEdited !== null) return { report: notEdited };
  const sdk = plan.sdk;
  switch (f.kind) {
    case 'tfm': {
      const next = nextTfmList(f.value, plan);
      if (next === null) return {};
      const reps: Replacement[] = [{ line: f.line, start: f.start, end: f.end, text: next }];
      const plural = next.includes(';');
      if (plural && f.element === 'TargetFramework') {
        // Element rename for `add` on a single-target project; the opening and closing names change too.
        reps.push({ line: f.line, start: f.elementStart!, end: f.elementEnd!, text: 'TargetFrameworks' });
        reps.push({ line: f.line, start: f.closeStart!, end: f.closeEnd!, text: 'TargetFrameworks' });
      }
      return { replacement: reps };
    }
    case 'global.json': {
      if (f.channel !== plan.fromChannel) return {};
      if (!plan.sdkVersion) return { report: 'no sdkVersion in the plan for global.json' };
      return { replacement: [{ line: f.line, start: f.start, end: f.end, text: plan.sdkVersion }] };
    }
    case 'package-version': {
      if (!plan.lockPackageMajor || f.channel !== plan.fromChannel) return {};
      const m = f.value.match(/^(\d+)\.\d+\.\d+(.*)$/);
      if (!m) return { report: 'package <Version> is not major.minor.patch' };
      const toMajor = plan.toChannel.split('.')[0];
      return { replacement: [{ line: f.line, start: f.start, end: f.end, text: `${toMajor}.0.0${m[2]}` }] };
    }
    case 'maven-release':
    case 'go.mod':
    case 'toolchain':
    case 'docker':
    case 'setup-action':
    case 'version-file':
    case 'tool-versions':
    case 'engines':
    case 'composer-platform':
    case 'gemfile-ruby':
    case 'rust-toolchain':
    // #606 M6: a matrix entry on the from-channel moves like any other pin; the matrix's older entries stay.
    case 'matrix': {
      if (f.channel !== plan.fromChannel) return {};
      // #606 D11: Rust's preview is the beta channel. A pinned toolchain moves to the dated beta the
      // plan names (`beta-2026-11-08`), since a beta has no stable version number to pin.
      if (sdk === 'rust' && plan.toVersion && isPreviewVersion(plan.toVersion) && (f.kind === 'rust-toolchain' || f.kind === 'version-file' || f.kind === 'setup-action' || f.kind === 'tool-versions')) {
        if (!plan.sdkVersion) return { report: `the plan moves to a Rust beta and names no toolchain (sdkVersion)` };
        return { replacement: [{ line: f.line, start: f.start, end: f.end, text: plan.sdkVersion }] };
      }
      // dotnet: a full SDK version (feature band >= 100, e.g. setup-dotnet `10.0.100`) takes the SDK
      // version of the plan, not the runtime version (S19: 10.0.100 -> 11.0.100-rc.1.26425.128).
      const sdkLike = sdk === 'dotnet' && f.kind === 'setup-action' && /^\d+\.\d+\.(\d{3,})/.test(f.value);
      if (sdkLike) {
        if (!plan.sdkVersion) return { report: `'${f.value}' is an SDK version and the plan has no sdkVersion` };
        return { replacement: [{ line: f.line, start: f.start, end: f.end, text: plan.sdkVersion }] };
      }
      const value = f.kind === 'engines' ? f.value.replace(/^(>=|\^|~)\s*/, '') : f.value;
      const prefix = f.value.slice(0, f.value.length - value.length);
      const next = rewriteVersionValue(sdk, value, plan.fromChannel, plan.toChannel, plan.toVersion);
      if (next === null) return { report: `cannot rewrite '${f.value}' without guessing (no ${plan.toChannel} version in the plan)` };
      return { replacement: [{ line: f.line, start: f.start, end: f.end, text: prefix + next }] };
    }
    default:
      return { report: 'unsupported location' };
  }
}

/**
 * Edits every editable location of one sdk atomically (R7). Computed or unsupported locations are
 * reported, never guessed (R8). Each file is rewritten from fresh scan positions, right to left, so
 * the bytes around every token are untouched.
 */
export async function applyPlatform(files: RepoFiles, plan: PlatformPlan): Promise<PlatformApplyResult> {
  const found = (await scanLocations(files, [plan.sdk])).filter((f) => inScope(f.file, plan));
  const byFile = new Map<string, Replacement[]>();
  const removals = new Map<string, number[]>();
  const reported: PlatformApplyResult['reported'] = [];
  const inChannel = plan.fromChannel === plan.toChannel;
  for (const f of found) {
    // Locations on other channels are not ours (e.g. a net48 test project, an older Docker stage).
    if (f.kind !== 'tfm' && f.channel !== null && f.channel !== plan.fromChannel) continue;
    if (f.kind === 'dotnet-quality') {
      // #617 D31: a computed quality (the step's only input, an expression) is reported only when it would go.
      if (f.value.toLowerCase() === 'ga' || !targetsRelease(plan)) continue;
      if (removesQuality(f, plan)) removals.set(f.file, [...(removals.get(f.file) ?? []), f.line]);
      else reported.push({ file: f.file, value: f.value, reason: notEditedReason(f) ?? 'not removed' });
      continue;
    }
    const { replacement, report } = replacementFor(f, plan);
    if (report) reported.push({ file: f.file, value: f.value, reason: report });
    // #617 D29: an in-channel move only ever moves a pin forward; a TFM or a floating pin it rewrites to itself stays.
    const changing = replacement?.filter((r) => r.text !== f.value && !(inChannel && replacement.length === 1 && !isNewerVersion(r.text, f.value)));
    if (changing?.length) byFile.set(f.file, [...(byFile.get(f.file) ?? []), ...changing]);
  }
  const edited: string[] = [];
  for (const file of new Set([...byFile.keys(), ...removals.keys()])) {
    const doc = parseText(await files.readBytes(file), file);
    const reps = byFile.get(file) ?? [];
    reps.sort((a, b) => b.line - a.line || b.start - a.start);
    for (const r of reps) {
      const line = doc.lines[r.line];
      line.text = line.text.slice(0, r.start) + r.text + line.text.slice(r.end);
    }
    for (const at of [...new Set(removals.get(file) ?? [])].sort((a, b) => b - a)) {
      const [gone] = doc.lines.splice(at, 1);
      if (gone.eol === '' && at > 0) doc.lines[at - 1].eol = '';
    }
    await files.write(file, serializeText(doc));
    edited.push(file);
  }
  return { edited: edited.sort(), reported };
}
