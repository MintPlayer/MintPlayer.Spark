import type { PlanItem } from '../contracts/types';
import type { Adapter } from './adapter';
import { npmAdapter, pnpmAdapter, yarnAdapter } from './adapters/js';
import { gradleAdapter, mavenAdapter } from './adapters/jvm';
import { cargoAdapter, composerAdapter, goAdapter } from './adapters/native';
import { nugetAdapter } from './adapters/nuget';
import { pipAdapter, pipCompileAdapter, poetryAdapter, uvAdapter } from './adapters/python';
import { replaceStringAdapter, versionFileAdapter } from './adapters/replaceString';

/**
 * First match wins (D-S12d: datasource + lockFiles + manifest file name). The order puts the more
 * specific matcher first (pnpm/yarn before npm, pip-compile before pip).
 */
export const ADAPTERS: readonly Adapter[] = [
  replaceStringAdapter,
  versionFileAdapter,
  pnpmAdapter,
  yarnAdapter,
  npmAdapter,
  nugetAdapter,
  uvAdapter,
  poetryAdapter,
  pipCompileAdapter,
  pipAdapter,
  composerAdapter,
  mavenAdapter,
  gradleAdapter,
  cargoAdapter,
  goAdapter,
];

export function adapterFor(item: PlanItem, adapters: readonly Adapter[] = ADAPTERS): Adapter | null {
  return adapters.find((a) => a.matches(item)) ?? null;
}
