import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import type { Plan, PlanGroup } from '../contracts/types';
import { item } from '../testing/helpers';
import type { Exec, ExecOptions } from '../util/exec';
import { ensureDotnetSdk, requiredDotnetSdk } from './dotnetSdk';

/**
 * Dotnet.Tools (2026-11-13): no root global.json, so the apply workflow's setup-dotnet step is skipped and every NuGet
 * group of a net11.0 repository failed with NETSDK1045 on the runner's 10.0 SDK. The apply now installs the SDK itself.
 */
function repo(files: Record<string, string>): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dotnet-sdk-'));
  for (const [f, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(root, f)), { recursive: true });
    fs.writeFileSync(path.join(root, f), text);
  }
  return root;
}

const csproj = (tfm: string) => `<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>${tfm}</TargetFramework>\n  </PropertyGroup>\n</Project>\n`;
const nugetGroup: PlanGroup = {
  group: 'microsoft',
  items: [item({ depName: 'Microsoft.Extensions.Hosting', datasource: 'nuget', packageFile: 'src/A/A.csproj', currentValue: '10.0.5', newValue: '11.0.0' })],
};
const npmGroup: PlanGroup = {
  group: 'npm',
  items: [item({ depName: 'left-pad', datasource: 'npm', packageFile: 'package.json', currentValue: '1.0.0', newValue: '1.1.0' })],
};
const plan = (...groups: PlanGroup[]): Plan => ({ contractVersion: '1', kind: 'plan', planId: 'p', expectedSha: 'a'.repeat(40), groups }) as unknown as Plan;

function recorder(answers: { sdks?: string; installCode?: number } = {}) {
  const calls: Array<{ cmd: string; args: string[]; env?: ExecOptions['env'] }> = [];
  const exec: Exec = async (cmd, args, options) => {
    calls.push({ cmd, args: [...args], env: options.env });
    if (cmd === 'dotnet' && args[0] === '--list-sdks') return { code: 0, stdout: answers.sdks ?? '', stderr: '' };
    if (cmd === 'bash' || cmd === 'powershell') return { code: answers.installCode ?? 0, stdout: '', stderr: answers.installCode ? 'install failed' : '' };
    return { code: 0, stdout: '', stderr: '' };
  };
  return { exec, calls };
}

describe('requiredDotnetSdk', () => {
  it('is the highest TargetFramework major of a repository whose plan has NuGet work', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0'), 'src/B/B.csproj': csproj('net10.0;net9.0').replace('TargetFramework>', 'TargetFrameworks>').replace('</TargetFramework>', '</TargetFrameworks>') });
    expect(await requiredDotnetSdk(plan(nugetGroup), root)).toEqual({ major: 11, version: null });
  });

  it('is the platform group\'s SDK version when it moves the repository to a later major', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net10.0') });
    const platform: PlanGroup = { group: 'dotnet', items: [], platform: { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', sdkVersion: '11.0.100' } };
    expect(await requiredDotnetSdk(plan(nugetGroup, platform), root)).toEqual({ major: 11, version: '11.0.100' });
  });

  it('is null without .NET work, and null when the repository has a root global.json (setup-dotnet installed it)', async () => {
    expect(await requiredDotnetSdk(plan(npmGroup), repo({ 'src/A/A.csproj': csproj('net11.0') }))).toBeNull();
    expect(await requiredDotnetSdk(plan(nugetGroup), repo({ 'src/A/A.csproj': csproj('net11.0'), 'global.json': '{ "sdk": { "version": "11.0.100" } }' }))).toBeNull();
  });
});

describe('ensureDotnetSdk', () => {
  const installDir = path.join(os.tmpdir(), 'mp-dotnet-test');

  it('installs the channel of the highest TFM when no installed SDK has that major, and puts it first on PATH', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0') });
    const { exec, calls } = recorder({ sdks: '10.0.403 [/usr/share/dotnet/sdk]\n' });
    const run = await ensureDotnetSdk(plan(nugetGroup), { root, exec, installDir, platform: 'linux' });
    const install = calls.find((c) => c.cmd === 'bash')!;
    expect(install.args).toEqual([path.join(installDir, 'dotnet-install.sh'), '--channel', '11.0', '--install-dir', installDir, '--no-path']);
    expect(calls.find((c) => c.cmd === 'curl')!.args).toContain('https://dot.net/v1/dotnet-install.sh');

    await run('dotnet', ['restore'], { cwd: root });
    const restore = calls[calls.length - 1];
    expect(restore.env?.DOTNET_ROOT).toBe(installDir);
    expect(restore.env?.PATH?.split(path.delimiter)[0]).toBe(installDir);
  });

  it('installs the platform group\'s exact SDK version', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net10.0') });
    const platform: PlanGroup = { group: 'dotnet', items: [], platform: { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', sdkVersion: '11.0.100' } };
    const { exec, calls } = recorder({ sdks: '10.0.403 [/usr/share/dotnet/sdk]\n' });
    await ensureDotnetSdk(plan(nugetGroup, platform), { root, exec, installDir, platform: 'linux' });
    expect(calls.find((c) => c.cmd === 'bash')!.args).toEqual([path.join(installDir, 'dotnet-install.sh'), '--version', '11.0.100', '--install-dir', installDir, '--no-path']);
  });

  it('installs nothing and returns the exec unchanged when an installed SDK already has the major', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0') });
    const { exec, calls } = recorder({ sdks: '10.0.403 [/usr/share/dotnet/sdk]\n11.0.100 [/usr/share/dotnet/sdk]\n' });
    expect(await ensureDotnetSdk(plan(nugetGroup), { root, exec, installDir, platform: 'linux' })).toBe(exec);
    expect(calls.map((c) => c.cmd)).toEqual(['dotnet']);
  });

  it('a failed install is logged and leaves the exec unchanged (the groups then fail with the SDK error, as before)', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0') });
    const { exec } = recorder({ sdks: '10.0.403 [/usr/share/dotnet/sdk]\n', installCode: 1 });
    const lines: string[] = [];
    expect(await ensureDotnetSdk(plan(nugetGroup), { root, exec, installDir, platform: 'linux', log: (l) => lines.push(l) })).toBe(exec);
    expect(lines.join('\n')).toMatch(/could not install the \.NET 11 SDK.*install failed/s);
  });

  it('a failed script download skips the install', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0') });
    const base = recorder({ sdks: '' });
    const exec: Exec = async (cmd, args, options) => (cmd === 'curl' ? { code: 22, stdout: '', stderr: 'curl: (22) 404' } : base.exec(cmd, args, options));
    const lines: string[] = [];
    expect(await ensureDotnetSdk(plan(nugetGroup), { root, exec, installDir, platform: 'linux', log: (l) => lines.push(l) })).toBe(exec);
    expect(base.calls.some((c) => c.cmd === 'bash')).toBe(false);
    expect(lines.join('\n')).toMatch(/could not install.*curl: \(22\) 404/s);
  });

  it('uses dotnet-install.ps1 on Windows', async () => {
    const root = repo({ 'src/A/A.csproj': csproj('net11.0') });
    const { exec, calls } = recorder({ sdks: '' });
    await ensureDotnetSdk(plan(nugetGroup), { root, exec, installDir, platform: 'win32' });
    const install = calls.find((c) => c.cmd === 'powershell')!;
    expect(install.args).toEqual(['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(installDir, 'dotnet-install.ps1'), '-Channel', '11.0', '-InstallDir', installDir]);
  });

  it('does nothing without .NET work', async () => {
    const { exec, calls } = recorder();
    expect(await ensureDotnetSdk(plan(npmGroup), { root: repo({}), exec, installDir, platform: 'linux' })).toBe(exec);
    expect(calls).toEqual([]);
  });
});
