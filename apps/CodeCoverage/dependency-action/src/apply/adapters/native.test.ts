import type { PlanItem } from '../../contracts/types';
import { ctxFor, fakeExec, item, tempDir, writeTree } from '../../testing/helpers';
import { cargoAdapter, cargoLockHas, COMPOSER_FLAGS, composerAdapter, goAdapter } from './native';

/**
 * The Cargo, Go and Composer adapters' tool steps with the tool faked: Cargo's pin-first and the major it leaves to the
 * relock, `cargo metadata --locked` as the verify, go mod tidy under GOTOOLCHAIN=local, and Composer's --with pins with
 * scripts and plugins off (D-S14c).
 */

function repo(files: Record<string, string>): string {
  const dir = tempDir('dep-native-');
  writeTree(dir, files);
  return dir;
}

const crate = (partial: Partial<PlanItem> = {}): PlanItem =>
  item({ depName: 'serde', datasource: 'crate', manager: 'cargo', packageFile: 'Cargo.toml', currentValue: '1.0.190', newValue: '1.0.200', ...partial });
const LOCK = (version: string) => `[[package]]\nname = "serde"\nversion = "${version}"\n`;

describe('cargo adapter', () => {
  it('pins first with cargo update --precise, skipping a crate without a lock and tolerating a major', async () => {
    const dir = repo({ 'Cargo.lock': LOCK('1.0.190'), 'sub/Cargo.toml': '' });
    const { exec, calls } = fakeExec(() => ({ code: 101, stderr: 'error: failed to select a version for the requirement `serde = "^1.0.190"`' }));

    expect((await cargoAdapter.pin!(ctxFor(dir, exec), [crate(), crate({ packageFile: 'sub/Cargo.toml' })])).ok).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0].args).toEqual(['update', '-p', 'serde', '--precise', '1.0.200']);

    const other = await cargoAdapter.pin!(ctxFor(dir, fakeExec(() => ({ code: 101, stderr: 'network down' })).exec), [crate()]);
    expect(other.ok).toBe(false);
  });

  it('relocks only a crate the lock does not hold yet', async () => {
    const pinned = repo({ 'Cargo.lock': LOCK('1.0.200') });
    const stale = repo({ 'Cargo.lock': LOCK('1.0.190') });
    const first = fakeExec(() => ({ code: 0 }));
    const second = fakeExec(() => ({ code: 1 }));

    expect((await cargoAdapter.relock!(ctxFor(pinned, first.exec), [crate()])).ok).toBe(true);
    expect(first.calls).toHaveLength(0);
    expect((await cargoAdapter.relock!(ctxFor(stale, second.exec), [crate()])).ok).toBe(false);
  });

  it('verifies the lock, then cargo metadata --locked once per directory', async () => {
    const dir = repo({ 'Cargo.lock': LOCK('1.0.200') });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await cargoAdapter.verify!(ctxFor(dir, exec), [crate(), crate({ depName: 'itoa', newVersion: '1.0.10', lockFiles: ['none/Cargo.lock'] })])).ok).toBe(true);
    expect(calls.map((c) => c.args[0])).toEqual(['metadata']);
    expect((await cargoAdapter.verify!(ctxFor(repo({ 'Cargo.lock': LOCK('1.0.190') }), exec), [crate()])).output).toContain('serde 1.0.200');
    expect((await cargoAdapter.verify!(ctxFor(dir, fakeExec(() => ({ code: 1 })).exec), [crate()])).ok).toBe(false);
  });

  it('checks consistency and names its files and tools', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await cargoAdapter.consistency!(ctxFor(repo({}), exec), '.')).ok).toBe(true);
    expect(calls[0].args).toEqual(['metadata', '--locked', '--format-version', '1']);
    expect(await cargoAdapter.files(ctxFor('.'), crate({ packageFile: 'a/Cargo.toml' }))).toEqual(['a/Cargo.toml', 'a/Cargo.lock']);
    expect(cargoAdapter.consistencyDir!(crate())).toBe('.');
    expect(cargoAdapter.tools(crate())).toEqual(['cargo']);
    expect(cargoLockHas(LOCK('1.0.200'), 'serde', '1.0.200')).toBe(true);
    expect(cargoAdapter.parseBlockers('', ctxFor('.'), [crate()])).toEqual([]);
  });
});

describe('go adapter', () => {
  const mod = (partial: Partial<PlanItem> = {}): PlanItem =>
    item({ depName: 'github.com/google/uuid', datasource: 'go', manager: 'gomod', packageFile: 'go.mod', currentValue: 'v1.3.0', newValue: 'v1.4.0', newVersion: 'v1.4.0', ...partial });

  it('tidies each module directory once under the local toolchain', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await goAdapter.relock!(ctxFor(repo({}), exec), [mod(), mod({ depName: 'golang.org/x/net' })])).ok).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0].args).toEqual(['mod', 'tidy']);
    expect((await goAdapter.relock!(ctxFor(repo({}), fakeExec(() => ({ code: 1, stderr: 'go: go.mod requires go >= 1.23' })).exec), [mod()])).ok).toBe(false);
  });

  it('verifies tidy kept the planned version', async () => {
    const dir = repo({ 'go.mod': 'module x\n\nrequire github.com/google/uuid v1.4.0\n' });
    expect((await goAdapter.verify!(ctxFor(dir), [mod()])).ok).toBe(true);
    expect((await goAdapter.verify!(ctxFor(dir), [mod({ newValue: 'v1.5.0' })])).output).toContain('tidy changed it');
  });

  it('checks consistency with go mod verify and lists go.sum', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await goAdapter.consistency!(ctxFor(repo({}), exec), '.')).ok).toBe(true);
    expect(calls[0].args).toEqual(['mod', 'verify']);
    expect(await goAdapter.files(ctxFor('.'), mod({ packageFile: 'svc/go.mod' }))).toEqual(['svc/go.mod', 'svc/go.sum']);
    expect(goAdapter.consistencyDir!(mod())).toBe('.');
    expect(goAdapter.tools(mod())).toEqual(['go']);
  });
});

describe('composer adapter', () => {
  const pkg = (partial: Partial<PlanItem> = {}): PlanItem =>
    item({ depName: 'monolog/monolog', datasource: 'packagist', manager: 'composer', packageFile: 'composer.json', currentValue: '^2.9', newValue: '^2.10', newVersion: '2.10.0', ...partial });
  const lock = (version: string) => JSON.stringify({ packages: [], 'packages-dev': [{ name: 'Monolog/Monolog', version }] });

  it('updates with --with pins and every safety flag, only where a lock exists', async () => {
    const dir = repo({ 'composer.lock': lock('2.9.0') });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await composerAdapter.relock!(ctxFor(dir, exec), [pkg(), pkg({ packageFile: 'other/composer.json' })])).ok).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0].args).toEqual(['update', 'monolog/monolog', '--with', 'monolog/monolog:2.10.0', ...COMPOSER_FLAGS]);
    expect((await composerAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 2 })).exec), [pkg()])).ok).toBe(false);
  });

  it('verifies the lock across packages and packages-dev, ignoring a v prefix', async () => {
    expect((await composerAdapter.verify!(ctxFor(repo({ 'composer.lock': lock('v2.10.0') })), [pkg()])).ok).toBe(true);
    expect((await composerAdapter.verify!(ctxFor(repo({ 'composer.lock': lock('2.9.0') })), [pkg()])).output).toContain('lock has 2.9.0');
    expect((await composerAdapter.verify!(ctxFor(repo({})), [pkg()])).ok).toBe(true);
  });

  it('validates, then dry-runs an install', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await composerAdapter.consistency!(ctxFor(repo({}), exec), '.')).ok).toBe(true);
    expect(calls.map((c) => c.args[0])).toEqual(['validate', 'install']);
    expect((await composerAdapter.consistency!(ctxFor(repo({}), fakeExec(() => ({ code: 1 })).exec), '.')).ok).toBe(false);
    expect(await composerAdapter.files(ctxFor('.'), pkg())).toEqual(['composer.json', 'composer.lock']);
    expect(composerAdapter.consistencyDir!(pkg())).toBe('.');
    expect(composerAdapter.tools(pkg())).toEqual(['composer']);
  });
});
