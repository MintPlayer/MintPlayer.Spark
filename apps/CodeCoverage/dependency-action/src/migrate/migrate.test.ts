import * as fs from 'node:fs';
import * as path from 'node:path';
import { isValid } from '../contracts/validate';
import { commitAll, fakeExec, gitRepo, tempDir } from '../testing/helpers';
import { realExec } from '../util/exec';
import { extraBumps, migrateSteps, MIGRATE_ENV, MigrateStepFailed, runMigrate } from './migrate';

const NG = [
  { name: '@angular/core', from: '21.2.0', to: '22.2.3' },
  { name: '@angular/cli', from: '21.2.27', to: '22.2.3' },
];
const NX = [
  { name: 'nx', from: '22.7.12', to: '23.3.0' },
  { name: '@nx/js', from: '22.7.12', to: '23.3.0' },
];

describe('migrateSteps: the S17 sequence', () => {
  it('Angular: install first, local CLI per package, --allow-dirty from the second, then relock', () => {
    expect(migrateSteps('angular', NG, 'npm').map((s) => [s.cmd, ...s.args].join(' '))).toEqual([
      'npm ci --ignore-scripts --no-audit --no-fund',
      'npx --no-install ng update @angular/core --migrate-only --from=21.2.0 --to=22.2.3',
      'npx --no-install ng update @angular/cli --migrate-only --from=21.2.27 --to=22.2.3 --allow-dirty',
      'npm install --package-lock-only --ignore-scripts --no-audit --no-fund',
    ]);
  });

  it('Nx: --from (nx#23505), --include=all (R19), install with scripts off, run-migrations, relock', () => {
    expect(migrateSteps('nx', NX, 'npm').map((s) => [s.cmd, ...s.args].join(' '))).toEqual([
      'npm ci --ignore-scripts --no-audit --no-fund',
      'npx --no-install nx migrate 23.3.0 --from=nx@22.7.12,@nx/js@22.7.12 --include=all --agentic=false',
      'npm install --ignore-scripts --no-audit --no-fund',
      'npx --no-install nx migrate --run-migrations --agentic=false',
      'npm install --package-lock-only --ignore-scripts --no-audit --no-fund',
    ]);
  });

  it('pnpm and yarn use their own frozen install and exec', () => {
    expect(migrateSteps('nx', NX, 'pnpm')[0].args).toEqual(['install', '--frozen-lockfile', '--ignore-scripts']);
    expect(migrateSteps('nx', NX, 'pnpm')[1].args.slice(0, 3)).toEqual(['exec', 'nx', 'migrate']);
    expect(migrateSteps('angular', NG, 'yarn')[0]).toMatchObject({ cmd: 'corepack', args: ['yarn', 'install', '--immutable', '--mode=skip-build'] });
  });

  it('runs every step with scripts off, no analytics, no daemon, no cloud and the version check disabled', () => {
    expect(MIGRATE_ENV).toMatchObject({ npm_config_ignore_scripts: 'true', NG_CLI_ANALYTICS: 'false', NG_DISABLE_VERSION_CHECK: '1', NX_DAEMON: 'false', NX_NO_CLOUD: 'true' });
  });
});

function ngRepo() {
  return gitRepo({
    'package.json': JSON.stringify({ name: 'app', dependencies: { '@angular/core': '^22.2.3' }, devDependencies: { typescript: '~5.9.2' } }, null, 2) + '\n',
    'package-lock.json': '{}\n',
    'src/app.ts': "import { Input } from '@angular/core';\n",
    '.gitignore': 'node_modules\n',
  });
}

describe('runMigrate', () => {
  it('writes migrate.patch and, LAST, a schema-valid completion marker with the extra bumps (R19)', async () => {
    const { dir, sha } = ngRepo();
    const out = tempDir();
    const order: string[] = [];
    const { exec } = fakeExec((cmd, args, cwd) => {
      order.push([cmd, ...args].slice(0, 4).join(' '));
      if (args.includes('ng') && args.includes('@angular/core')) {
        fs.writeFileSync(path.join(cwd, 'src/app.ts'), "import { input } from '@angular/core';\n");
        fs.mkdirSync(path.join(cwd, 'node_modules/x'), { recursive: true });
        fs.writeFileSync(path.join(cwd, 'node_modules/x/index.js'), '');
      }
      if (cmd === 'npm' && args.includes('--package-lock-only')) {
        const pkg = JSON.parse(fs.readFileSync(path.join(cwd, 'package.json'), 'utf8'));
        pkg.devDependencies.typescript = '6.0.3';
        fs.writeFileSync(path.join(cwd, 'package.json'), JSON.stringify(pkg, null, 2) + '\n');
      }
      return { code: 0 };
    });
    const { result, resultFile, patchFile } = await runMigrate({ exec, repoDir: dir, outDir: out, tool: 'angular', packages: NG.slice(0, 1), baseSha: sha });
    expect(order).toEqual(['npm ci --ignore-scripts --no-audit', 'npx --no-install ng update', 'npm install --package-lock-only --ignore-scripts']);
    expect(isValid('migrate', JSON.parse(fs.readFileSync(resultFile, 'utf8'))).errors).toBe('');
    expect(result.extraBumps).toEqual([{ depName: 'typescript', packageFile: 'package.json', from: '~5.9.2', to: '6.0.3' }]);
    const patch = fs.readFileSync(patchFile, 'utf8');
    expect(patch).toContain("+import { input } from '@angular/core';");
    expect(patch).not.toContain('node_modules');
  });

  it('a failing step aborts the run and writes NO marker (S17 partial-migration detection)', async () => {
    const { dir, sha } = ngRepo();
    const out = tempDir();
    const { exec, calls } = fakeExec((cmd, args) => (args.includes('ng') ? { code: 1, stderr: 'EPERM: tsconfig.app.json' } : { code: 0 }));
    await expect(runMigrate({ exec, repoDir: dir, outDir: out, tool: 'angular', packages: NG, baseSha: sha })).rejects.toBeInstanceOf(MigrateStepFailed);
    expect(fs.existsSync(path.join(out, 'migrate-result.json'))).toBe(false);
    // Stopped at the first failing package: the second `ng update` and the relock never ran.
    expect(calls.filter((c) => c.cmd !== 'git').map((c) => `${c.cmd} ${c.args[0]}`)).toEqual(['npm ci', 'npx --no-install']);
  });

  it('refuses a checkout that is not base_sha, and package specs outside the contract', async () => {
    const { dir, sha } = ngRepo();
    commitAll(dir, 'moved');
    await expect(runMigrate({ exec: realExec, repoDir: dir, outDir: tempDir(), tool: 'nx', packages: NX, baseSha: sha })).rejects.toThrow(/is not base_sha/);
    const head = gitRepo({ a: '1' });
    await expect(
      runMigrate({ exec: realExec, repoDir: head.dir, outDir: tempDir(), tool: 'nx', packages: [{ name: 'nx', from: '22', to: '23 --dangerous' }], baseSha: head.sha }),
    ).rejects.toThrow(/migrate does not match its contract/);
  });
});

describe('extraBumps', () => {
  it('lists dependency changes outside the group in every package.json, own packages excluded', async () => {
    const { dir } = gitRepo({
      'package.json': '{ "dependencies": { "nx": "22.7.12", "a": "1.0.0" } }\n',
      'libs/x/package.json': '{ "devDependencies": { "b": "^1.0.0" } }\n',
    });
    fs.writeFileSync(path.join(dir, 'package.json'), '{ "dependencies": { "nx": "23.3.0", "a": "1.0.0", "c": "2.0.0" } }\n');
    fs.writeFileSync(path.join(dir, 'libs/x/package.json'), '{ "devDependencies": { "b": "^2.0.0" } }\n');
    expect(await extraBumps(realExec, dir, NX)).toEqual([
      { depName: 'b', packageFile: 'libs/x/package.json', from: '^1.0.0', to: '^2.0.0' },
      { depName: 'c', packageFile: 'package.json', from: null, to: '2.0.0' },
    ]);
  });
});
