import type { Plan, PlatformPlan } from '../contracts/types';

/** #658: the one file the plan commit may change besides the plan. */
export const GLOBAL_JSON = 'global.json';

const SDK_VERSION = /"sdk"\s*:\s*\{[^{}]*?"version"\s*:\s*"([^"]*)"/;

/** The plan's only unscoped .NET platform group with an SDK version: the one the bot pre-sets `global.json` for. */
export function presetPlatform(plan: Plan): PlatformPlan | null {
  const dotnet = plan.groups.map((g) => g.platform).filter((p): p is PlatformPlan => p?.sdk === 'dotnet' && !!p.sdkVersion);
  if (dotnet.length !== 1) return null;
  const [only] = dotnet;
  return only.locations?.length || only.exclude?.length ? null : only;
}

/** `text` with only its `sdk.version` value replaced, or null without one. */
export function withSdkVersion(text: string, version: string): string | null {
  const m = SDK_VERSION.exec(text);
  if (!m) return null;
  const start = m.index + m[0].length - 1 - m[1].length;
  return text.slice(0, start) + version + text.slice(start + m[1].length);
}

/**
 * #658: the plan commit's `global.json` change is the bot's pre-set (`GlobalJsonPreset.cs`) when it moves nothing but
 * `sdk.version`, to the SDK of the plan's .NET platform group. Why not, or null when it is.
 */
export function presetRefusal(plan: Plan, before: string, after: string): string | null {
  const platform = presetPlatform(plan);
  if (!platform) return `the plan has no single unscoped .NET platform group to move ${GLOBAL_JSON} for`;
  const m = SDK_VERSION.exec(before);
  if (!m || m[1] === platform.sdkVersion) return `${GLOBAL_JSON} has no sdk.version to move`;
  if (withSdkVersion(before, platform.sdkVersion!) !== after) return `${GLOBAL_JSON} changes more than sdk.version -> ${platform.sdkVersion}`;
  return null;
}
