import { readFixture } from '../testing/helpers';
import {
  gradleFailed, parseCargo, parseComposer, parseGo, parseGradle, parseMaven, parseNpm, parseNuGetAssets, parseNuGetText, parsePip, parsePnpmNdjson, parsePoetry, parseUv, parseYarnJson,
} from './blockers';

/** Every case is a REAL capture (S13 conflicts, S14 resolver errors); see the SOURCE.md next to each. */
describe('blocker parsers on real resolver output', () => {
  it('npm ERESOLVE (S13, --json + prose): the declaring package is a planned one, so the peer it wants blocks', () => {
    expect(parseNpm(readFixture('apply/npm/conflict-eresolve.json.log'), ['react-dom'])).toEqual([
      { depName: 'react', version: '17.0.2', code: 'ERESOLVE', message: 'peer react@"^18.2.0" from react-dom@18.2.0; found react@17.0.2' },
    ]);
  });

  it('npm ERESOLVE: an unplanned declarer (the @nx/angular case) is the blocker itself', () => {
    expect(parseNpm(readFixture('resolver-errors/npm/eresolve-peer-conflict.txt'), ['evil-pkg'])[0]).toMatchObject({ depName: 'peerhost', version: '1.0.0', code: 'ERESOLVE' });
  });

  it('npm "overriding peer dependency" WARNING is a blocker too (D-C2)', () => {
    expect(parseNpm(readFixture('resolver-errors/npm/eresolve-overriding-peer-warning.txt'), ['evil-pkg'])[0]).toMatchObject({ depName: 'peerhost', code: 'ERESOLVE' });
  });

  it('pnpm ndjson pnpm:peer-dependency-issues (structured), with and without the declarer planned', () => {
    const log = readFixture('apply/pnpm/conflict-peer.ndjson.log');
    const message = 'unmet peer react@^18.2.0: found 17.0.2 (required by react-dom@18.2.0)';
    expect(parsePnpmNdjson(log)).toEqual([{ depName: 'react-dom', version: '18.2.0', code: 'ERR_PNPM_PEER_DEP_ISSUES', message }]);
    expect(parsePnpmNdjson(log, ['react-dom'])).toEqual([{ depName: 'react', version: '17.0.2', code: 'ERR_PNPM_PEER_DEP_ISSUES', message }]);
  });

  it('pnpm text warning form (S14)', () => {
    expect(parsePnpmNdjson(readFixture('resolver-errors/pnpm/unmet-peer-warning.txt'))[0]).toMatchObject({ depName: 'peerhost', version: '1.0.0', code: 'ERR_PNPM_PEER_DEP_ISSUES' });
  });

  it('yarn --json YN0060 (structured), with and without the declarer planned', () => {
    const log = readFixture('apply/yarn/conflict-peer.ndjson.log');
    const message = "react is listed by your project with version 17.0.2 (pcb45e), which doesn't satisfy what react-dom requests (^18.2.0).";
    expect(parseYarnJson(log)).toEqual([{ depName: 'react-dom', version: null, code: 'YN0060', message }]);
    expect(parseYarnJson(log, ['react-dom'])).toEqual([{ depName: 'react', version: '17.0.2', code: 'YN0060', message }]);
  });

  it('NuGet project.assets.json logs[] (structured, errors only)', () => {
    expect(parseNuGetAssets(readFixture('apply/nuget/conflict-NU1605.project.assets.json'))).toEqual([
      expect.objectContaining({ depName: 'Serilog', version: '4.0.0', code: 'NU1605' }),
    ]);
  });

  it.each([
    ['apply/nuget/conflict-NU1605.restore.log', 'Serilog', 'NU1605'],
    ['resolver-errors/nuget/NU1605-downgrade.txt', 'EvilNuget', 'NU1605'],
    ['resolver-errors/nuget/NU1102-missing-version.txt', 'EvilNuget', 'NU1102'],
  ])('NuGet restore text %s', (file, depName, code) => {
    const b = parseNuGetText(readFixture(file));
    expect(b).toHaveLength(1);
    expect(b[0]).toMatchObject({ depName, code });
  });

  it('Composer Problem blocks (S13 and S14)', () => {
    const s13 = readFixture('apply/composer/conflict.log');
    expect(parseComposer(s13)[0]).toMatchObject({ depName: 'monolog/monolog', version: '3.5.0', code: 'composer' });
    // monolog 3.5.0 is the planned package; what it requires (psr/log ^2 || ^3) is what the root pins.
    expect(parseComposer(s13, ['monolog/monolog'])[0]).toMatchObject({ depName: 'psr/log', version: '^2.0 || ^3.0' });
    expect(parseComposer(readFixture('resolver-errors/composer/conflict-no-matching-version.txt'))).toEqual([expect.objectContaining({ depName: 'evil/plugin', version: '9.9.9' })]);
  });

  it('Cargo "failed to select a version"', () => {
    const log = readFixture('apply/cargo/conflict.log');
    expect(parseCargo(log)).toEqual([expect.objectContaining({ depName: 'serde', version: '1.0.200', code: 'cargo' })]);
    expect(parseCargo(log, ['serde'])).toEqual([expect.objectContaining({ depName: 'serde_derive', version: '1.0.190' })]);
  });

  it('Go: a newer-toolchain requirement is a `go` blocker (a platform blocker, R7); a missing version names the module', () => {
    expect(parseGo(readFixture('apply/go/conflict-toolchain.log'))).toEqual([expect.objectContaining({ depName: 'go', version: '1.24.0', code: 'go-toolchain' })]);
    expect(parseGo(readFixture('resolver-errors/go/missing-version.txt'))).toEqual([expect.objectContaining({ depName: 'example.com/evilmod', version: 'v9.9.9' })]);
  });

  it('Maven missing artifact (S13 and S14)', () => {
    expect(parseMaven(readFixture('apply/maven/conflict-missing-version.log'))).toEqual([expect.objectContaining({ depName: 'com.google.guava:guava', version: '99.0.0-jre' })]);
    expect(parseMaven(readFixture('resolver-errors/maven/missing-version.txt'))[0]).toMatchObject({ depName: 'com.evil:dep', version: '9.9.9' });
  });

  it('Gradle prints FAILED and exits 0 (S13): detected and attributed', () => {
    const log = readFixture('apply/gradle/conflict-missing-version.log');
    expect(log).toContain('BUILD SUCCESSFUL');
    expect(gradleFailed(log)).toBe(true);
    expect(parseGradle(log)).toEqual([expect.objectContaining({ depName: 'com.google.guava:guava', version: '99.0.0-jre', code: 'gradle-FAILED' })]);
  });

  it('pip ResolutionImpossible', () => {
    expect(parsePip(readFixture('apply/pip/conflict-ResolutionImpossible.log'))).toEqual([expect.objectContaining({ depName: 'requests', version: '2.31.0', code: 'ResolutionImpossible' })]);
  });

  it('Poetry SolverProblemError', () => {
    expect(parsePoetry(readFixture('apply/poetry/conflict-SolverProblemError.log'))).toEqual([expect.objectContaining({ depName: 'flask', version: '3.0.0', code: 'SolverProblemError' })]);
  });

  it('uv no-solution, missing version and --no-build (S13/S14)', () => {
    expect(parseUv(readFixture('apply/uv/conflict-no-solution.log'))[0]).toMatchObject({ depName: 'flask', version: '3.0.0', code: 'uv-no-solution' });
    expect(parseUv(readFixture('resolver-errors/uv/no-solution-missing-version.txt'))[0]).toMatchObject({ depName: 'evilpkg', version: '9.9.9', code: 'uv-no-version' });
    expect(parseUv(readFixture('resolver-errors/uv/no-build-no-wheel.txt'))[0]).toMatchObject({ depName: 'evilpkg', version: '1.1.0', code: 'uv-no-build' });
  });

  it.each([parseNpm, parsePnpmNdjson, parseYarnJson, parseNuGetText, parseComposer, parseCargo, parseGo, parseMaven, parseGradle, parsePip, parsePoetry, parseUv])(
    'unrecognised output -> no blockers (%o)',
    (parse) => {
      expect((parse as (s: string) => unknown[])('something completely different happened\nexit 1')).toEqual([]);
    },
  );
});
