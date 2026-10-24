export interface Version {
  major: number;
  minor: number;
  patch: number;
  prerelease: string[];
  text: string;
}

const SEMVER = /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z.-]{1,128}))?(?:\+[0-9A-Za-z.-]{1,128})?$/;

export function parse(text: string): Version | null {
  const m = SEMVER.exec(text);
  if (!m) return null;
  return { major: +m[1], minor: +m[2], patch: +m[3], prerelease: m[4] ? m[4].split('.') : [], text };
}

/** SemVer 2.0 precedence; build metadata is ignored. */
export function compare(a: Version, b: Version): number {
  for (const key of ['major', 'minor', 'patch'] as const) if (a[key] !== b[key]) return a[key] - b[key];
  if (a.prerelease.length === 0 || b.prerelease.length === 0) return b.prerelease.length - a.prerelease.length;
  for (let i = 0; i < Math.max(a.prerelease.length, b.prerelease.length); i++) {
    const x = a.prerelease[i];
    const y = b.prerelease[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const nx = /^\d+$/.test(x);
    const ny = /^\d+$/.test(y);
    if (nx && ny) {
      if (+x !== +y) return +x - +y;
    } else if (nx !== ny) return nx ? -1 : 1;
    else if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

export function highest(versions: string[]): Version | null {
  return versions.map(parse).filter((v): v is Version => v !== null).sort(compare).at(-1) ?? null;
}

/**
 * The npm dist-tag a release goes out under (PRD D11): a prerelease is `next`; a stable release of the highest stable
 * major is `latest`; a stable release of an older major (a release-branch fix) is `v<major>-lts`, so it never takes
 * `latest` from the current line.
 */
export function distTag(version: string, released: string[]): string {
  const v = parse(version);
  if (!v) throw new Error(`'${version}' is not a semantic version.`);
  if (v.prerelease.length > 0) return 'next';
  const stableMajors = [...released, version].map(parse).filter((x): x is Version => x !== null && x.prerelease.length === 0).map((x) => x.major);
  return v.major >= Math.max(...stableMajors) ? 'latest' : `v${v.major}-lts`;
}
