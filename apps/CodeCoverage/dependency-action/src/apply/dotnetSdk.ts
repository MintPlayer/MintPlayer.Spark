import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import type { Plan } from '../contracts/types';
import { scanLocations } from '../platform/locations';
import { combined, type Exec } from '../util/exec';
import { directoryFiles } from '../util/repoFiles';
import { GLOBAL_JSON } from './sdkPreset';

/**
 * The apply installs the .NET SDK a repository without a root `global.json` needs (Dotnet.Tools, 2026-11-13). The apply
 * workflow's `setup-dotnet` step reads `global.json` and is skipped without one, so the runner's preinstalled SDK
 * (10.0.403) restored the net11.0 projects and every NuGet group failed with NETSDK1045. A repository is not made to
 * add a `global.json` for this (#649 D13: the bot never creates one).
 */
export interface DotnetSdkNeed {
  major: number;
  /** The exact SDK the plan's .NET platform group moves to, else null: the latest of the major's channel. */
  version: string | null;
}

const INSTALL_SCRIPT = 'https://dot.net/v1/dotnet-install';

const majorOf = (v: string | null | undefined): number => (v ? Number(v.split('.')[0]) || 0 : 0);

/** The SDK major the plan's .NET work needs, or null without .NET work or with a root global.json. */
export async function requiredDotnetSdk(plan: Plan, root: string): Promise<DotnetSdkNeed | null> {
  const platforms = plan.groups.map((g) => g.platform).filter((p) => p?.sdk === 'dotnet');
  const nuget = plan.groups.some((g) => g.items.some((i) => i.datasource === 'nuget'));
  if (!nuget && platforms.length === 0) return null;
  if (await exists(path.join(root, GLOBAL_JSON))) return null;
  let major = 0;
  for (const f of await scanLocations(directoryFiles(root), ['dotnet'])) if (f.kind === 'tfm') major = Math.max(major, majorOf(f.channel));
  let version: string | null = null;
  for (const p of platforms) {
    const target = Math.max(majorOf(p!.sdkVersion), majorOf(p!.toChannel));
    if (target >= major) {
      major = target;
      version = p!.sdkVersion && majorOf(p!.sdkVersion) === target ? p!.sdkVersion : null;
    }
  }
  return major > 0 ? { major, version } : null;
}

export interface EnsureDotnetSdkOptions {
  root: string;
  exec: Exec;
  log?: (line: string) => void;
  /** Where the SDK goes; default `$RUNNER_TEMP/mintplayer-dotnet`. */
  installDir?: string;
  platform?: NodeJS.Platform;
}

/**
 * Installs the needed SDK when no installed one has its major, and returns an Exec whose children find it first
 * (PATH, DOTNET_ROOT). Returns `exec` itself when nothing is needed or the install fails: the groups then fail with the
 * SDK's own error, as they did before.
 */
export async function ensureDotnetSdk(plan: Plan, o: EnsureDotnetSdkOptions): Promise<Exec> {
  const log = o.log ?? (() => undefined);
  const need = await requiredDotnetSdk(plan, o.root);
  if (!need) return o.exec;
  const listed = await o.exec('dotnet', ['--list-sdks'], { cwd: o.root });
  const installed = listed.code === 0 ? listed.stdout.split('\n').map((l) => majorOf(l.trim().split(' ')[0])) : [];
  if (installed.some((m) => m >= need.major)) return o.exec;

  const dir = o.installDir ?? path.join(process.env.RUNNER_TEMP || os.tmpdir(), 'mintplayer-dotnet');
  const windows = (o.platform ?? process.platform) === 'win32';
  const script = path.join(dir, windows ? 'dotnet-install.ps1' : 'dotnet-install.sh');
  const wanted = need.version ?? `${need.major}.0`;
  await fs.mkdir(dir, { recursive: true });
  const download = await o.exec('curl', ['-sSLf', '--retry', '3', '-o', script, `${INSTALL_SCRIPT}${windows ? '.ps1' : '.sh'}`], { cwd: o.root });
  const install =
    download.code !== 0
      ? download
      : windows
        ? await o.exec('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, need.version ? '-Version' : '-Channel', wanted, '-InstallDir', dir], { cwd: o.root })
        : await o.exec('bash', [script, need.version ? '--version' : '--channel', wanted, '--install-dir', dir, '--no-path'], { cwd: o.root });
  if (install.code !== 0) {
    log(`dotnet: could not install the .NET ${need.major} SDK (${wanted}); the runner's SDKs are used:\n${combined(install)}`);
    return o.exec;
  }
  log(`dotnet: no ${GLOBAL_JSON} and no installed .NET ${need.major} SDK; installed ${wanted} into ${dir}`);
  return (command, args, options) =>
    o.exec(command, args, {
      ...options,
      env: { ...options.env, DOTNET_ROOT: dir, PATH: [dir, options.env?.PATH ?? process.env.PATH ?? ''].join(path.delimiter) },
    });
}

async function exists(file: string): Promise<boolean> {
  return fs.access(file).then(
    () => true,
    () => false,
  );
}
