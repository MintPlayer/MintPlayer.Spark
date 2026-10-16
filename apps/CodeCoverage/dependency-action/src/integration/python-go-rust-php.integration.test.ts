import { pathToFileURL } from 'node:url';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { item, tempDir } from '../testing/helpers';
import { cargoLocalRegistry, composerArtifacts, goProxy, wheelDir } from './registries';
import { applyOnRepo, enabled, must } from './setup';

/**
 * python+go+rust+php slice: uv with --find-links (no index), Go with GOPROXY=file://...,off, Cargo with a
 * local-registry source replacing crates.io, Composer with an `artifact` repository and packagist off.
 */

describe.skipIf(!enabled('python+go+rust+php', 'uv'))('uv against a --find-links directory', () => {
  const links = tempDir();
  beforeAll(() => wheelDir(links, [{ name: 'liba', version: '1.0.0' }, { name: 'liba', version: '1.1.0' }, { name: 'liba', version: '1.2.0' }]));

  it('pins a range with `uv lock --upgrade-package n==v` and keeps the pyproject pure', async () => {
    const env = { UV_NO_INDEX: '1', UV_FIND_LINKS: links, UV_CACHE_DIR: tempDir(), UV_PYTHON_DOWNLOADS: 'never' };
    const pyproject = '[project]\nname = "it"\nversion = "0.1.0"\nrequires-python = ">=3.8"\ndependencies = [\n    "liba>=1.0.0",   # range\n]\n';
    const run = await applyOnRepo(
      { 'pyproject.toml': pyproject },
      [{ group: 'liba', items: [item({ depName: 'liba', datasource: 'pypi', manager: 'pep621', packageFile: 'pyproject.toml', lockFiles: ['uv.lock'], currentValue: '>=1.0.0', newValue: '>=1.1.0', newVersion: '1.1.0' })] }],
      { env, prepare: (dir) => must('uv', ['lock', '--upgrade-package', 'liba==1.0.0'], dir, env).then(() => undefined) },
    );
    expect(run.result.groups[0].status).toBe('applied');
    expect(run.read('pyproject.toml')).toBe(pyproject.replace('"liba>=1.0.0"', '"liba>=1.1.0"'));
    expect(run.read('uv.lock')).toMatch(/name = "liba"\r?\nversion = "1\.1\.0"/);
  });

  it('a version that does not exist is unresolvable with a uv blocker', async () => {
    const env = { UV_NO_INDEX: '1', UV_FIND_LINKS: links, UV_CACHE_DIR: tempDir(), UV_PYTHON_DOWNLOADS: 'never' };
    const run = await applyOnRepo(
      { 'pyproject.toml': '[project]\nname = "it"\nversion = "0.1.0"\nrequires-python = ">=3.8"\ndependencies = ["liba==1.0.0"]\n' },
      [{ group: 'liba', items: [item({ depName: 'liba', datasource: 'pypi', manager: 'pep621', packageFile: 'pyproject.toml', lockFiles: ['uv.lock'], currentValue: '==1.0.0', newValue: '==9.9.9', newVersion: '9.9.9' })] }],
      { env, prepare: (dir) => must('uv', ['lock'], dir, env).then(() => undefined) },
    );
    expect(run.result.groups[0]).toMatchObject({ status: 'unresolvable', blockers: [expect.objectContaining({ depName: 'liba', code: 'uv-no-version' })] });
  });
});

describe.skipIf(!enabled('python+go+rust+php', 'go'))('Go against GOPROXY=file://', () => {
  const proxy = tempDir();
  beforeAll(() => goProxy(proxy, [
    { path: 'example.com/liba', version: 'v1.0.0' }, { path: 'example.com/liba', version: 'v1.1.0' },
    { path: 'example.com/newgo', version: 'v1.0.0' }, { path: 'example.com/newgo', version: 'v1.1.0', goVersion: '1.999' },
  ]));
  const env = () => ({ GOPROXY: `${pathToFileURL(proxy).href},off`, GONOSUMDB: '*', GONOSUMCHECK: '1', GOFLAGS: '-mod=mod', GOSUMDB: 'off', GOPATH: tempDir(), GOTOOLCHAIN: 'local' });
  const main = 'package main\n\nimport (\n\t_ "example.com/liba"\n\t_ "example.com/newgo"\n)\n\nfunc main() {}\n';
  const gomod = 'module example.com/it\n\ngo 1.21\n\nrequire (\n\texample.com/liba v1.0.0 // pinned\n\texample.com/newgo v1.0.0\n)\n';
  const goItem = (dep: string, to: string) => item({ depName: dep, datasource: 'go', manager: 'gomod', packageFile: 'go.mod', currentValue: 'v1.0.0', newValue: to, newVersion: to });

  it('text edit + go mod tidy: comment kept, go.sum updated; a newer-go module is a `go` blocker', async () => {
    const run = await applyOnRepo({ 'go.mod': gomod, 'main.go': main }, [
      { group: 'liba', items: [goItem('example.com/liba', 'v1.1.0')] },
      { group: 'newgo', items: [goItem('example.com/newgo', 'v1.1.0')] },
    ], { env: env(), prepare: (dir) => must('go', ['mod', 'tidy'], dir, env()).then(() => undefined) });
    expect(run.result.groups.map((g) => g.status)).toEqual(['applied', 'unresolvable']);
    expect(run.read('go.mod')).toContain('\texample.com/liba v1.1.0 // pinned\n');
    expect(run.read('go.sum')).toContain('example.com/liba v1.1.0');
    expect(run.result.groups[1].blockers[0]).toMatchObject({ depName: 'go', code: 'go-toolchain' });
  });
});

describe.skipIf(!enabled('python+go+rust+php', 'cargo'))('Cargo against a local-registry', () => {
  const reg = tempDir();
  beforeAll(() => cargoLocalRegistry(reg, [
    { name: 'liba', version: '1.0.0' }, { name: 'liba', version: '1.0.5' }, { name: 'liba', version: '1.0.9' },
  ]));

  it('pins first (`cargo update --precise`), then edits: exact, never the latest compatible (S13)', async () => {
    const config = `[source.crates-io]\nreplace-with = "local"\n\n[source.local]\nlocal-registry = "${reg.replace(/\\/g, '/')}"\n\n[net]\noffline = true\n`;
    const toml = '[package]\nname = "it"\nversion = "0.1.0"\nedition = "2021"\n\n[dependencies]\nliba = "1.0.0"   # lib\n';
    const env = { CARGO_HOME: tempDir() };
    const run = await applyOnRepo(
      { 'Cargo.toml': toml, 'src/main.rs': 'fn main() {}\n', '.cargo/config.toml': config },
      [{ group: 'liba', items: [item({ depName: 'liba', datasource: 'crate', manager: 'cargo', packageFile: 'Cargo.toml', lockFiles: ['Cargo.lock'], currentValue: '1.0.0', newValue: '1.0.5' })] }],
      {
        env,
        prepare: async (dir) => {
          await must('cargo', ['generate-lockfile'], dir, env);
          await must('cargo', ['update', '-p', 'liba', '--precise', '1.0.0'], dir, env);
        },
      },
    );
    expect(run.result.groups[0].status).toBe('applied');
    expect(run.read('Cargo.toml')).toBe(toml.replace('liba = "1.0.0"', 'liba = "1.0.5"'));
    expect(run.read('Cargo.lock')).toMatch(/name = "liba"\r?\nversion = "1\.0\.5"/);
  });
});

describe.skipIf(!enabled('python+go+rust+php', 'composer'))('Composer against an artifact repository', () => {
  const art = tempDir();
  const sentinel = path.join(tempDir(), 'COMPOSER-SENTINEL');
  beforeAll(() => composerArtifacts(art, [
    { name: 'acme/liba', version: '1.0.0' }, { name: 'acme/liba', version: '1.1.0' }, { name: 'acme/liba', version: '1.2.0' },
    // D-S14c regression: a plugin must never run (--no-plugins), nor a script (--no-scripts).
    { name: 'acme/plugin', version: '1.0.0', type: 'composer-plugin', require: { 'composer-plugin-api': '^2.0' }, extra: { class: 'Acme\\Plugin' } },
  ]));

  it('`composer update n --with n:v` pins a range exactly, plugins and scripts off', async () => {
    const composer = (liba: string) => `{\n    "name": "acme/it",\n    "require": {\n        "acme/liba": "${liba}",\n        "acme/plugin": "1.0.0"\n    },\n    "scripts": { "post-update-cmd": "php -r \\"file_put_contents('${sentinel.replace(/\\/g, '/')}', 'ran');\\"" },\n    "repositories": [ { "type": "artifact", "url": "${art.replace(/\\/g, '/')}" }, { "packagist.org": false } ],\n    "config": { "allow-plugins": { "acme/plugin": true } }\n}\n`;
    const env = { COMPOSER_HOME: tempDir(), COMPOSER_CACHE_DIR: tempDir() };
    const run = await applyOnRepo(
      { 'composer.json': composer('^1.0') },
      [{ group: 'liba', items: [item({ depName: 'acme/liba', datasource: 'packagist', manager: 'composer', packageFile: 'composer.json', lockFiles: ['composer.lock'], currentValue: '^1.0', newValue: '^1.1', newVersion: '1.1.0' })] }],
      { env, prepare: (dir) => must('composer', ['update', '--no-install', '--no-scripts', '--no-plugins', '--with', 'acme/liba:1.0.0'], dir, env).then(() => undefined) },
    );
    expect(run.result.groups[0].status).toBe('applied');
    expect(run.read('composer.json')).toBe(composer('^1.1'));
    expect(JSON.parse(run.read('composer.lock')).packages.find((p: { name: string }) => p.name === 'acme/liba').version).toBe('1.1.0');
    expect(fs.existsSync(sentinel)).toBe(false);
  });
});
