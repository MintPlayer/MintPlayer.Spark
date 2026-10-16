import * as fs from 'node:fs';
import * as path from 'node:path';
import { fakeExec, readFixture, tempDir } from '../testing/helpers';
import { dockerArgs, RENOVATE_IMAGE, renovateConfig, runRenovate } from './renovate';

describe('Renovate invocation', () => {
  it('pins the S12 image by digest (R13)', () => {
    expect(RENOVATE_IMAGE).toBe('renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77');
    expect(readFixture('renovate/npm/SOURCE.md')).toContain('sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77');
  });

  it('extends the group presets and separates every bucket (D-C1, D-S12b)', () => {
    const c = renovateConfig();
    expect(c.extends).toEqual(['group:monorepos', 'group:recommended']);
    expect(c).toMatchObject({ separateMultipleMajor: true, separateMinorPatch: true, separateMultipleMinor: true });
  });

  it('runs lookup only, writes a file report, and passes secrets by NAME', () => {
    const args = dockerArgs({ repoDir: '/repo', outDir: '/out', configFile: '/cfg.json', image: RENOVATE_IMAGE, githubTokenEnv: 'INPUT_GITHUB-TOKEN', hostRulesEnv: 'INPUT_HOST-RULES' });
    expect(args).toEqual(expect.arrayContaining(['--platform=local', '--dry-run=lookup', '--binary-source=install', '--report-type=file', '--report-path=/out/report.json', RENOVATE_IMAGE]));
    expect(args).toContain('RENOVATE_GITHUB_COM_TOKEN');
    expect(args).toContain('RENOVATE_HOST_RULES');
    expect(args.some((a) => a.includes('='))).toBe(true);
    // Never `-e NAME=value` for a secret.
    expect(args.filter((a) => /^RENOVATE_(GITHUB_COM_TOKEN|HOST_RULES)=/.test(a))).toEqual([]);
    expect(args.indexOf(RENOVATE_IMAGE)).toBeLessThan(args.indexOf('--platform=local'));
  });

  it('reads the report the container wrote, and fails loudly on a non-zero exit', async () => {
    const work = tempDir();
    const report = readFixture('renovate/npm/report.json');
    const ok = fakeExec((cmd, args) => {
      expect(cmd).toBe('docker');
      const out = args[args.indexOf('-v', args.indexOf('-v') + 1) + 1].split(':/out')[0];
      fs.writeFileSync(path.join(out, 'report.json'), report);
      return { code: 0 };
    });
    process.env.TEST_TOKEN_NAME = 'secret-value';
    const run = await runRenovate({ exec: ok.exec, repoDir: work, workDir: work, githubTokenEnv: 'TEST_TOKEN_NAME' });
    expect(Object.keys(run.report.repositories)).toEqual(['local']);
    expect(ok.calls[0].args.join(' ')).not.toContain('secret-value');

    const bad = fakeExec(() => ({ code: 1, stderr: 'boom' }));
    await expect(runRenovate({ exec: bad.exec, repoDir: work, workDir: work })).rejects.toThrow(/renovate exited 1/);
  });
});
