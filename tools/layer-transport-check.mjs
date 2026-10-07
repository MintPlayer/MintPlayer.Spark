#!/usr/bin/env node
// `npm run test:layer-transport` — the same application over ProjectReference and over a local-feed
// PackageReference sees the same library layers (composition PRD G4, D1; spike S1 kept as a check).
//
// 1. Packs the Spark libraries an application needs (the ProjectReference closure of
//    MintPlayer.Spark and MintPlayer.Spark.Authorization, plus the generator) at a throw-away version
//    into a local feed. Build output goes to --artifacts-path, so the repo's bin/obj and the Nx cache
//    are untouched, and a private NuGet packages folder keeps the throw-away version out of the
//    machine's cache.
// 2. Generates two console apps outside the repository (so no Directory.Build.* applies): AppProj
//    references the libraries by ProjectReference, AppPkg by PackageReference from the feed.
// 3. Builds and runs both. Each prints what the build recorded ([assembly: SparkLayerAssemblies]) and
//    what the run time found (SparkLayerCatalog): alias|assembly|dependsOn|kind|path|length|sha256.
// 4. Fails unless both outputs are identical and carry the core actions layer and the Authorization
//    translations layer.
//
// A script rather than an xunit test: packing ~15 projects and two restores take minutes and need
// nuget.org, which the local sweep (`npm run test:affected`) must not wait for. Run it when the
// transport changes, and in the final sweep (composition plan M11).
//
// Usage: node tools/layer-transport-check.mjs [workDir]   (default: a new directory under the OS temp)
// Every step's raw output is kept in <workDir>/logs.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const work = path.resolve(process.argv[2] ?? fs.mkdtempSync(path.join(os.tmpdir(), 'spark-layer-transport-')));
const version = '0.0.0-layercheck';
const roots = [
  'libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj',
  'libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj',
];
const generator = 'libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj';

const feed = path.join(work, 'feed');
const logs = path.join(work, 'logs');
for (const dir of [feed, logs]) fs.mkdirSync(dir, { recursive: true });

/** Runs a command with its raw output in logs/<name>.log; stops the check when it fails. */
function run(name, command, args, cwd = work) {
  const log = path.join(logs, `${name}.log`);
  const fd = fs.openSync(log, 'w');
  const result = spawnSync(command, args, { cwd, stdio: ['ignore', fd, fd], shell: false });
  fs.closeSync(fd);
  console.log(`${name}: exit ${result.status}`);
  if (result.status !== 0) {
    console.error(`[layer-transport] ${name} failed, see ${log}`);
    process.exit(1);
  }
  return fs.readFileSync(log, 'utf8');
}

/** The packable ProjectReference closure of the roots, by reading the csproj files. */
function closure(projects) {
  const seen = new Set();
  const queue = projects.map(p => path.join(repo, p));
  while (queue.length > 0) {
    const project = path.normalize(queue.shift());
    if (seen.has(project)) continue;
    seen.add(project);
    const text = fs.readFileSync(project, 'utf8');
    for (const match of text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g))
      queue.push(path.resolve(path.dirname(project), match[1].replace(/\\/g, '/')));
  }
  return [...seen].filter(p => !/<IsPackable>\s*false\s*<\/IsPackable>/i.test(fs.readFileSync(p, 'utf8')));
}

const packed = closure([...roots, generator]);
for (const project of packed) {
  run(`pack-${path.basename(project, '.csproj')}`, 'dotnet', [
    'pack', project, '-c', 'Release', '-o', feed, `-p:Version=${version}`,
    '--artifacts-path', path.join(work, 'artifacts'),
  ]);
}

// The generator dll the packs built; read now, because AppProj's build rebuilds it without -p:Version.
const generatorName = path.basename(generator, '.csproj');
const builtGenerator = fs.readFileSync(path.join(work, 'artifacts', 'bin', generatorName, 'release', `${generatorName}.dll`));

// Keep MSBuild from looking above the work directory, and NuGet on the feed plus nuget.org.
fs.writeFileSync(path.join(work, 'Directory.Build.props'), '<Project />\n');
fs.writeFileSync(path.join(work, 'Directory.Build.targets'), '<Project />\n');
fs.writeFileSync(path.join(work, 'nuget.config'), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config><add key="globalPackagesFolder" value="${path.join(work, 'packages')}" /></config>
  <packageSources>
    <clear />
    <add key="layercheck" value="${feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
`);

const program = `using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;

var recorded = Assembly.GetEntryAssembly()!.GetCustomAttribute<SparkLayerAssembliesAttribute>()?.AssemblyNames ?? [];
Console.WriteLine("recorded: " + string.Join(", ", recorded));
foreach (var library in SparkLayerCatalog.Libraries)
    foreach (var layer in library.Layers)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layer.Json)))[..16];
        Console.WriteLine($"{library.Alias}|{library.AssemblyName}|{string.Join("+", library.DependsOn)}|{layer.Kind}|{layer.Path}|{layer.Json.Length}|{hash}");
    }
`;

const common = `
    <TargetFramework>net11.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableSparkAgentsGuide>false</EnableSparkAgentsGuide>
    <EnableSparkAuthSpa>false</EnableSparkAuthSpa>`;

const apps = {
  AppProj: `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>${common}
  </PropertyGroup>
  <ItemGroup>
${roots.map(r => `    <ProjectReference Include="${path.join(repo, r)}" />`).join('\n')}
    <ProjectReference Include="${path.join(repo, generator)}" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
`,
  AppPkg: `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>${common}
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MintPlayer.Spark" Version="${version}" />
    <PackageReference Include="MintPlayer.Spark.Authorization" Version="${version}" />
    <PackageReference Include="MintPlayer.Spark.SourceGenerators" Version="${version}" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
  </ItemGroup>
</Project>
`,
};

const outputs = {};
for (const [name, csproj] of Object.entries(apps)) {
  const dir = path.join(work, name);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, `${name}.csproj`), csproj);
  fs.writeFileSync(path.join(dir, 'Program.cs'), program);
  run(`build-${name}`, 'dotnet', ['build', dir, '-c', 'Release', '--artifacts-path', path.join(work, 'artifacts')]);
  const dll = path.join(work, 'artifacts', 'bin', name, 'release', `${name}.dll`);
  outputs[name] = run(`run-${name}`, 'dotnet', [dll]).replace(/\r\n/g, '\n').trim();
}

// AppPkg must have run the generator this check built. MintPlayer.SourceGenerators.Tools' props pack a
// fixed bin/<Configuration> path, so before Directory.Build.targets' SparkPackBuiltGenerator the package
// silently carried a stale bin/Release build from before ApplicationLayersGenerator, and AppPkg recorded
// nothing (measured 2026-10-07, composition PRD §9). NuGet restore extracted the package here.
{
  const restored = path.join(work, 'packages', generatorName.toLowerCase(), version, 'analyzers', 'dotnet', 'roslyn5.9', 'cs', `${generatorName}.dll`);
  if (!fs.existsSync(restored) || !fs.readFileSync(restored).equals(builtGenerator)) {
    console.error(`[layer-transport] FAILED: the ${generatorName} package does not carry the generator this run built (${restored})`);
    process.exit(1);
  }
}

const expected = [
  /^recorded: MintPlayer\.Spark, /m,
  /^spark\|MintPlayer\.Spark\|\|actions\|actions\.json\|/m,
  /^authorization\|MintPlayer\.Spark\.Authorization\|[^|]*\|translations\|translations\.json\|/m,
];
const missing = expected.filter(e => !e.test(outputs.AppProj));
if (outputs.AppProj !== outputs.AppPkg || missing.length > 0) {
  console.error('[layer-transport] FAILED');
  if (missing.length > 0) console.error(`AppProj lacks: ${missing.join(', ')}`);
  console.error(`--- AppProj (ProjectReference)\n${outputs.AppProj}\n--- AppPkg (PackageReference)\n${outputs.AppPkg}`);
  process.exit(1);
}

console.log(`[layer-transport] identical over ProjectReference and PackageReference (${work}):\n${outputs.AppProj}`);
