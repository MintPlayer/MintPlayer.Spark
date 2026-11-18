#!/usr/bin/env node
// The spark-app template matrix (docs/templates_dnx_terminal_PRD.md §4.6, D12; plan M4).
// LOCAL ONLY, by the owner's decision (2026-11-17): CI never runs it. Documented in the templates README.
//
// Every variant is generated from the template packed at THIS commit and restored from a feed packed at this commit,
// never nuget.org's Spark packages, so the matrix catches a template that a change in a library broke.
//
//   node tools/template-matrix.mjs [--out <dir>] [--only <variant,…>] [--parallel <n>] [--no-client] [--ng-spark <tgz>]
//
// Phases:
//   1. Pack: the template with -p:SparkTemplateFeedVersion, which packs every package it pins into <out>/feed at a
//      version unique to this run. Unique, because NuGet caches a package by id and version: a fixed version would
//      restore a previous run's bytes from the global packages folder.
//   2. .NET, per variant: dotnet new, restore from the feed (and nuget.org for third-party packages), build without the
//      client (EnableSpaBuilder=false), --spark-synchronize-model, --spark-verify-model. Neither flag starts the host or
//      touches the database, so no RavenDB server is needed.
//   3. Client, per distinct client: the ClientApp's package.json is the same in every variant and only a few sources
//      have #if regions, so one shared node_modules serves them all and each distinct set of sources is built once.
//      --ng-spark <tgz> installs a local `npm pack` of @mintplayer/ng-spark there, for a same-commit client.
//
// Variants come from libs/templates/MintPlayer.Spark.Templates/features.json: the minimal and default apps, OIDC, all
// features on, and each feature alone (its prerequisites follow from the template's own Use{X} symbols).
//
// Output: <out>/<variant>/ and a log per step in <out>/logs/. Exit code 1 when any step failed.
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync, copyFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const repo = resolve(import.meta.dirname, '..');
const templateProject = join(repo, 'libs/templates/MintPlayer.Spark.Templates');
const manifest = JSON.parse(readFileSync(join(templateProject, 'features.json'), 'utf8'));

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : fallback;
};
const out = resolve(option('--out', join(tmpdir(), 'spark-template-matrix')));
const parallel = Number(option('--parallel', '4'));
const only = option('--only', '')?.split(',').filter(Boolean) ?? [];
const withClient = !args.includes('--no-client');
/** The variant that also gets every item template (RavenDB, because spark-index is RavenDB only). */
const ITEMS_VARIANT = 'Defaults';
const ngSparkTarball = option('--ng-spark', '');

/** Each variant: a name and its `dotnet new spark-app` arguments. */
function variants() {
  const all = [
    { name: 'MinimalInMemory', args: ['--store', 'InMemory', '--no-client'] },
    { name: 'MinimalRaven', args: ['--migrations', 'false'] },
    { name: 'Defaults', args: [] },
    { name: 'Oidc', args: ['--auth', 'oidc', '--oidc-authority', 'https://localhost:5011'] },
    {
      name: 'AllOn',
      args: ['--auth', 'authorization', ...manifest.features.filter(f => f.flag).flatMap(f => [`--${f.flag}`, 'true'])],
    },
    ...manifest.features.map(f => ({
      name: `Only${f.id}`,
      args: f.symbol === 'HasAuth' ? ['--auth', 'authorization'] : [`--${f.flag}`, 'true'],
    })),
  ];
  return only.length ? all.filter(v => only.includes(v.name)) : all;
}

const logs = join(out, 'logs');

// npm through its own CLI script: spawning npm.cmd needs a shell on Windows, which concatenates the arguments (DEP0190).
const npmCli = [join(dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js'), join(dirname(process.execPath), '../lib/node_modules/npm/bin/npm-cli.js')]
  .find(existsSync);

/** Runs a command to a log file; resolves to { code, seconds }. */
function run(label, command, commandArgs, cwd) {
  const started = Date.now();
  const log = join(logs, `${label}.log`);
  return new Promise(done => {
    const child = spawn(command, commandArgs, { cwd, env: {
      ...process.env,
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      // --spark-synchronize-model runs with --no-launch-profile, which would otherwise leave the app in Production.
      ASPNETCORE_ENVIRONMENT: 'Development',
    } });
    const chunks = [];
    child.stdout.on('data', d => chunks.push(d));
    child.stderr.on('data', d => chunks.push(d));
    child.on('close', code => {
      writeFileSync(log, Buffer.concat(chunks));
      done({ code, seconds: Math.round((Date.now() - started) / 1000), log });
    });
  });
}

async function pool(items, size, work) {
  const results = [];
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(size, items.length) }, async () => {
    while (next < items.length) {
      const i = next++;
      results[i] = await work(items[i]);
    }
  }));
  return results;
}

const failures = [];
let selected = [];
const timings = [];

function record(name, step, result) {
  timings.push({ name, step, seconds: result.seconds, code: result.code });
  if (result.code !== 0) failures.push(`${name}: ${step} exited ${result.code} (${result.log})`);
  return result.code === 0;
}

rmSync(out, { recursive: true, force: true });
mkdirSync(logs, { recursive: true });

// 1. Pack.
const version = `11.0.0-matrix.${Date.now()}`;
const feed = join(out, 'feed');
const hive = join(out, 'hive');
const pack = await run('pack', 'dotnet', [
  'pack', templateProject, '-c', 'Release', '-o', feed, '-nologo',
  `-p:Version=${version}`, `-p:SparkTemplateFeedVersion=${version}`, `-p:SparkTemplateFeedDirectory=${feed}`,
], repo);
if (!record('feed', 'pack', pack)) finish();
const install = await run('install', 'dotnet', ['new', 'install', join(feed, `MintPlayer.Spark.Templates.${version}.nupkg`), '--debug:custom-hive', hive], repo);
if (!record('feed', 'install', install)) finish();

writeFileSync(join(out, 'nuget.config'), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="matrix" value="${feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="matrix"><package pattern="MintPlayer.Spark" /><package pattern="MintPlayer.Spark.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
`);

// 2. .NET.
selected = variants();
await pool(selected, parallel, async variant => {
  const dir = join(out, variant.name);
  const host = join(dir, variant.name, `${variant.name}.csproj`);
  const steps = [
    ['new', 'dotnet', ['new', 'spark-app', '-n', variant.name, '-o', dir, '--debug:custom-hive', hive, '--no-restore', ...variant.args], out],
    ['restore', 'dotnet', ['restore', join(dir, `${variant.name}.slnx`), '--configfile', join(out, 'nuget.config'), '-nologo'], dir],
    ...(variant.name === ITEMS_VARIANT ? [['items', () => addItems(variant.name, dir)]] : []),
    ['build','dotnet', ['build', join(dir, `${variant.name}.slnx`), '--no-restore', '-nologo', '-p:EnableSpaBuilder=false'], dir],
    ['synchronize', 'dotnet', ['run', '--project', host, '--no-build', '--no-launch-profile', '--', '--spark-synchronize-model'], join(dir, variant.name)],
    ['verify', 'dotnet', ['run', '--project', host, '--no-build', '--no-launch-profile', '--', '--spark-verify-model'], join(dir, variant.name)],
  ];
  for (const [step, command, commandArgs, cwd] of steps) {
    const result = typeof command === 'function' ? await command() : await run(`${variant.name}.${step}`, command, commandArgs, cwd);
    if (!record(variant.name, step, result)) return;
  }
});

/**
 * Adds every item template to the app the way a user would: each from its project directory, then wired as the
 * generated doc comments say (the lookup on the entity, the entity on the context). The build, synchronize and verify
 * steps that follow then cover the items too.
 */
async function addItems(name, dir) {
  const library = join(dir, `${name}.Library`);
  const app = join(dir, name);
  const items = [
    ['spark-entity', ['-n', 'Invoice'], library],
    ['spark-lookup', ['-n', 'InvoiceStatus'], library],
    ['spark-actions', ['-n', 'Invoice'], app],
    ['spark-custom-action', ['-n', 'Approve', '--entity', 'Invoice'], app],
    ['spark-index', ['-n', 'Invoice'], app],
  ];
  let seconds = 0;
  for (const [item, itemArgs, cwd] of items) {
    const result = await run(`${name}.item-${item}`, 'dotnet', ['new', item, ...itemArgs, '--debug:custom-hive', hive], cwd);
    seconds += result.seconds;
    if (result.code !== 0) return { ...result, seconds };
  }
  const entity = join(library, 'Entities/Invoice.cs');
  writeFileSync(entity, readFileSync(entity, 'utf8')
    .replace(/^namespace (.*)\.Entities;/m, 'using MintPlayer.Spark.Abstractions;\nusing $1.LookupReferences;\n\nnamespace $1.Entities;')
    .replace('    public string Name { get; set; } = string.Empty;',
      '    public string Name { get; set; } = string.Empty;\n\n    /// <summary>Where the invoice is.</summary>\n    [LookupReference(typeof(InvoiceStatus))]\n    public EInvoiceStatus? Status { get; set; }'));
  const context = join(app, `${name}Context.cs`);
  const contextSource = readFileSync(context, 'utf8');
  const wired = contextSource.replace('    public IQueryable<Person> People', '    public IQueryable<Invoice> Invoices => Session.Query<Invoice>();\n    public IQueryable<Person> People');
  writeFileSync(context, wired);
  const ok = wired !== contextSource;
  return { code: ok ? 0 : 1, seconds, log: ok ? '' : `${context} has no People root to add Invoices next to` };
}

// 3. Client.
if (withClient) {
  const clients = new Map();
  for (const variant of selected) {
    const clientApp = join(out, variant.name, variant.name, 'ClientApp');
    if (!existsSync(join(clientApp, 'package.json'))) continue;
    const hash = createHash('sha256');
    for (const file of ['src/app/app.config.ts', 'src/app/app.routes.ts', 'src/app/shell/shell.component.ts'])
      hash.update(readFileSync(join(clientApp, file), 'utf8').replaceAll(variant.name, 'SparkApp'));
    const key = hash.digest('hex');
    if (!clients.has(key)) clients.set(key, { variant: variant.name, clientApp });
  }

  const shared = join(out, 'client-modules');
  const first = [...clients.values()][0];
  if (first) {
    mkdirSync(shared, { recursive: true });
    copyFileSync(join(first.clientApp, 'package.json'), join(shared, 'package.json'));
    const ok = record('client', 'npm install', await run('npm-install', process.execPath, [npmCli, 'install', '--no-audit', '--no-fund'], shared))
      && (!ngSparkTarball || record('client', 'ng-spark', await run('npm-ng-spark', process.execPath, [npmCli, 'install', '--no-save', resolve(ngSparkTarball)], shared)));
    if (ok) {
      for (const { variant, clientApp } of clients.values()) {
        symlinkSync(join(shared, 'node_modules'), join(clientApp, 'node_modules'), 'junction');
        record(variant, 'ng build', await run(`${variant}.ng-build`, 'node', [join(shared, 'node_modules/@angular/cli/bin/ng.js'), 'build'], clientApp));
      }
    }
  }
  console.log(`Client builds: ${clients.size} distinct of ${selected.length} variants.`);
}

finish();

function finish() {
  for (const t of timings) console.log(`${t.code === 0 ? 'ok  ' : 'FAIL'} ${String(t.seconds).padStart(4)} s  ${t.name} ${t.step}`);
  if (failures.length) {
    console.log(`\n${failures.length} failure(s):\n${failures.join('\n')}`);
    process.exit(1);
  }
  console.log(`\nAll ${selected?.length ?? 0} variants passed.`);
  process.exit(0);
}
