import { parseMigratePackages, readInputs, runMode } from './main';

describe('inputs', () => {
  it('reads the INPUT_* environment the runner sets, with a RUNNER_TEMP default output dir', () => {
    const i = readInputs({ 'INPUT_MODE': 'apply', 'INPUT_EXPECTED-SHA': 'a'.repeat(40), 'INPUT_FEED-SECRETS': 'true', RUNNER_TEMP: '/tmp/rt', GITHUB_WORKSPACE: '/w' });
    expect(i).toMatchObject({ mode: 'apply', expectedSha: 'a'.repeat(40), feedSecrets: true, workspace: '/w' });
    expect(i.outputDir.replace(/\\/g, '/')).toBe('/tmp/rt/dependency-update');
  });

  it('parses migrate packages and refuses anything else', () => {
    expect(parseMigratePackages('[{"name":"nx","from":"22.7.12","to":"23.3.0"}]')).toEqual([{ name: 'nx', from: '22.7.12', to: '23.3.0' }]);
    expect(() => parseMigratePackages('nope')).toThrow(/JSON array/);
    expect(() => parseMigratePackages('[]')).toThrow(/non-empty/);
    expect(() => parseMigratePackages('[{"name":"nx"}]')).toThrow(/string name, from and to/);
  });

  it('refuses an unknown mode, a short sha and an unknown migrate tool before running anything', async () => {
    const base = { expectedSha: '', baseSha: '', outputDir: '/tmp/x', workspace: '.', feedSecrets: false, migrateTool: '', migratePackages: '' };
    const never = async () => {
      throw new Error('must not run');
    };
    await expect(runMode({ ...base, mode: 'push' }, never)).rejects.toThrow(/mode must be lookup, apply or migrate/);
    await expect(runMode({ ...base, mode: 'apply', expectedSha: 'abc' }, never)).rejects.toThrow(/full 40-hex/);
    await expect(runMode({ ...base, mode: 'migrate', migrateTool: 'react' }, never)).rejects.toThrow(/angular or nx/);
  });
});
