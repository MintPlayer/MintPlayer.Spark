import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { Exec } from '../util/exec';
import type { RenovateReport } from './mapReport';

/**
 * Renovate pinned by digest (R13, S12). A digest bump is a reviewed change: re-run the S12 sentinel
 * test (R17 guard 2) before moving it.
 */
export const RENOVATE_IMAGE =
  'renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77';

/**
 * The repository config handed to Renovate. D-C1: group:monorepos + group:recommended so related
 * packages carry one groupName. D-S12b: the separate* options so holds and cooldown have fallback
 * candidates (without them Renovate reports one candidate per bucket).
 */
/**
 * prerelease-dependencies-PRD H5: a SemVer prerelease (or an exact range around one, `[0.4.0-beta]`). Renovate tests it
 * against `lockedVersion ?? currentVersion ?? currentValue` (44.125.2 `lib/util/package-rules/current-version.ts:30-35`).
 * The engine screens candidates with the same pattern (`DependencyPolicyConfig.PrereleasePattern`).
 */
export const PRERELEASE_PATTERN = '^[\\[(v^~=]*\\d+(\\.\\d+)*-[0-9A-Za-z]';

/** H5: the datasources whose versions are SemVer, where `-` after the core always marks a prerelease (not docker's `20-alpine`). */
export const SEMVER_DATASOURCES = ['nuget', 'npm', 'crate', 'packagist'];

export function renovateConfig(unstable: readonly string[] = [], stable: readonly string[] = []): Record<string, unknown> {
  return {
    extends: ['group:monorepos', 'group:recommended'],
    separateMajorMinor: true,
    separateMultipleMajor: true,
    separateMinorPatch: true,
    separateMultipleMinor: true,
    // The local platform cannot read presets from the repo; a repo renovate.json is still merged.
    onboarding: false,
    requireConfig: 'optional',
    dependencyDashboard: false,
    // Later rules win (Renovate merges packageRules in order).
    // #606 D8: a package on the preview track (a `track: preview` rule, or a companion of an SDK on it) is offered prereleases.
    // prerelease-dependencies-PRD H1: so is a package already on a prerelease; Renovate's default keeps only the same
    // major.minor.patch (filter.ts:189-192), which silently dropped Octokit.GraphQL 0.3.0-beta → 0.4.0-beta (SP1).
    // H5: a `track: stable` rule (the file's, or an enforced account rule) opts out, so it comes last. Everything else keeps
    // Renovate's default of stable releases only: "unstable for everything" would displace stable candidates (SP2).
    packageRules: [
      ...(unstable.length > 0 ? [{ matchPackageNames: [...unstable], ignoreUnstable: false }] : []),
      { matchDatasources: [...SEMVER_DATASOURCES], matchCurrentVersion: `/${PRERELEASE_PATTERN}/`, ignoreUnstable: false },
      ...(stable.length > 0 ? [{ matchPackageNames: [...stable], ignoreUnstable: true }] : []),
    ],
  };
}

export interface RenovateRun {
  report: RenovateReport;
  seconds: number;
  log: string;
}

export interface RenovateOptions {
  exec: Exec;
  repoDir: string;
  workDir: string;
  /** GitHub token for github-tags/releases lookups (read-only; github.token is enough). Passed by NAME. */
  githubTokenEnv?: string;
  /** Env var NAME holding a RENOVATE_HOST_RULES JSON array (private feeds, §13.3). Passed by NAME. */
  hostRulesEnv?: string;
  image?: string;
  /** #606 D8: package globs offered prereleases. */
  unstable?: readonly string[];
  /** prerelease-dependencies-PRD H5: package globs looked up stable-only, even when already on a prerelease. */
  stable?: readonly string[];
}

/**
 * `docker run` arguments. Secrets travel as `-e NAME` (value inherited from this process's env),
 * never as `-e NAME=value`, so they never appear in a command line or a log.
 */
export function dockerArgs(o: { repoDir: string; outDir: string; configFile: string; image: string; user?: string; githubTokenEnv?: string; hostRules?: boolean }): string[] {
  const args = [
    'run', '--rm',
    ...(o.user ? ['--user', o.user] : []),
    '-v', `${o.repoDir}:/usr/src/app`,
    '-v', `${o.outDir}:/out`,
    '-v', `${o.configFile}:/config/renovate.json:ro`,
    '-w', '/usr/src/app',
    '-e', 'LOG_LEVEL=info',
    '-e', 'RENOVATE_CONFIG_FILE=/config/renovate.json',
    '-e', 'GIT_CONFIG_COUNT=1', '-e', 'GIT_CONFIG_KEY_0=safe.directory', '-e', 'GIT_CONFIG_VALUE_0=*',
  ];
  if (o.githubTokenEnv) args.push('-e', `RENOVATE_GITHUB_COM_TOKEN`);
  if (o.hostRules) args.push('-e', 'RENOVATE_HOST_RULES');
  args.push(
    o.image,
    '--platform=local',
    '--dry-run=lookup',
    '--binary-source=install',
    '--report-type=file',
    '--report-path=/out/report.json',
  );
  return args;
}

/**
 * The container runs as THIS process's uid, with group 0 (the image's own group, which keeps its tool
 * and cache directories writable for an arbitrary uid). The image's default user (12021) is nobody on
 * the host: a checkout that is not world-readable, such as an mkdtemp 0700 directory, made every git
 * call fail with `spawn git EACCES`, which Renovate reports as "git version needs upgrading". Undefined
 * where there are no POSIX uids (Windows), where Docker Desktop does not enforce host permissions.
 */
export function containerUser(getuid: (() => number) | null = process.getuid ?? null): string | undefined {
  return getuid ? `${getuid()}:0` : undefined;
}

/** The GitHub Packages NuGet feed (`nuget.pkg.github.com/<owner>`), the one a `nuget.config` maps an account's own packages to. */
const GITHUB_PACKAGES = 'nuget.pkg.github.com';

/**
 * follower-cooldown-PRD E3: the RENOVATE_HOST_RULES value. The job token reads GitHub Packages (the lookup template grants
 * `packages: read`, as Spark's CI restores from it), so a `nuget.config` that maps the account's packages there no longer
 * hides their updates behind a 401 (S4 a). The user's own rules come first and win: when one names that host, nothing
 * is added. Rules that aren't a JSON array are passed on as they are, for Renovate to refuse.
 */
export function hostRules(token: string | undefined, user: string | undefined, actor: string): string | undefined {
  if (!token) return user;
  let rules: unknown[] = [];
  if (user) {
    try {
      const parsed: unknown = JSON.parse(user);
      if (!Array.isArray(parsed)) return user;
      rules = parsed;
    } catch {
      return user;
    }
  }
  if (rules.some((r) => JSON.stringify(r).includes(GITHUB_PACKAGES))) return user;
  return JSON.stringify([...rules, { matchHost: GITHUB_PACKAGES, username: actor, password: token }]);
}

export async function runRenovate(o: RenovateOptions): Promise<RenovateRun> {
  const outDir = path.join(o.workDir, 'renovate-out');
  await fs.mkdir(outDir, { recursive: true });
  await fs.chmod(outDir, 0o777).catch(() => undefined);
  const configFile = path.join(o.workDir, 'renovate.json');
  await fs.writeFile(configFile, JSON.stringify(renovateConfig(o.unstable ?? [], o.stable ?? []), null, 2));
  const image = o.image ?? RENOVATE_IMAGE;
  const env: Record<string, string | undefined> = {};
  const token = o.githubTokenEnv ? process.env[o.githubTokenEnv] : undefined;
  if (o.githubTokenEnv) env.RENOVATE_GITHUB_COM_TOKEN = token;
  const rules = hostRules(token, o.hostRulesEnv ? process.env[o.hostRulesEnv] : undefined, process.env.GITHUB_ACTOR || 'x-access-token');
  if (rules !== undefined) env.RENOVATE_HOST_RULES = rules;
  const started = Date.now();
  const result = await o.exec('docker', dockerArgs({ repoDir: o.repoDir, outDir, configFile, image, user: containerUser(), githubTokenEnv: o.githubTokenEnv, hostRules: rules !== undefined }), {
    cwd: o.repoDir,
    env,
    timeoutMs: 30 * 60_000,
  });
  const seconds = Math.round((Date.now() - started) / 100) / 10;
  const log = `${result.stdout}\n${result.stderr}`;
  if (result.code !== 0) {
    throw new Error(`renovate exited ${result.code}:\n${log.slice(-4000)}`);
  }
  const report = JSON.parse(await fs.readFile(path.join(outDir, 'report.json'), 'utf8')) as RenovateReport;
  return { report, seconds, log };
}
