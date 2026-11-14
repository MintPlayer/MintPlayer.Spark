import * as fs from 'node:fs';
import * as path from 'node:path';
import { copyTree, diffPairs, fixture, readFixture, readJsonFixture, tempDir, writeTree, type LinePair } from '../testing/helpers';
import { directoryFiles } from '../util/repoFiles';
import { applyPlatform, nextTfmList, notEditedReason } from './applyPlatform';
import { channelOf, compareChannels, rewriteTfm, rewriteVersionValue } from './channels';
import { buildPlatformRecord, parsePlatformTable } from './feeds';
import { scanLocations } from './locations';

const NOW = new Date('2026-10-16T08:00:00Z');

describe('channels', () => {
  it.each([
    ['dotnet', '10.0.100', '10.0'],
    ['dotnet', '11.0.100-rc.1.26425.128', '11.0'],
    ['node', 'v20.11.0', '20'],
    ['node', '22.x', '22'],
    ['python', '3.11-slim', '3.11'],
    ['java', 'temurin-17.0.8+7', '17'],
    ['java', '1.8', '8'],
    ['go', '1.21.5', '1.21'],
    ['go', 'go1.22', '1.22'],
    ['node', 'lts/*', null],
    ['php', '8.4.14', '8.4'],
    ['php', '8.4-fpm', '8.4'],
    ['php', '8.6.0RC5', '8.6'],
    ['ruby', 'ruby-3.4.7', '3.4'],
    ['ruby', '4.1.0-preview1', '4.1'],
    ['rust', '1.99.0', '1.99'],
    ['rust', '1.100.0-beta.13', '1.100'],
    ['rust', 'stable', null],
  ] as const)('%s %s -> %s', (sdk, v, ch) => expect(channelOf(sdk, v)).toBe(ch));

  it('compares numerically', () => {
    expect(compareChannels('10.0', '9.0')).toBeGreaterThan(0);
    expect(compareChannels('3.9', '3.13')).toBeLessThan(0);
  });

  it.each([
    ['node', '20', '20', '22', null, '22'],
    ['node', '20.x', '20', '22', null, '22.x'],
    ['node', 'v20', '20', '22', null, 'v22'],
    ['node', '20.11.0', '20', '22', '22.21.0', '22.21.0'],
    ['node', '20.11.0-alpine', '20', '22', '22.21.0', '22.21.0-alpine'],
    ['dotnet', '10.0.x', '10.0', '11.0', null, '11.0.x'],
    ['dotnet', '10.0-alpine', '10.0', '11.0', null, '11.0-alpine'],
    ['python', '3.12-slim', '3.12', '3.13', null, '3.13-slim'],
    ['node', '20.11.0', '20', '22', null, null],
    ['node', '18', '20', '22', null, null],
    // The feed's build replaces the old one (R31 folds .tool-versions into the platform group).
    ['java', 'temurin-17.0.8+7', '17', '25', '25.0.4.1+1', 'temurin-25.0.4.1+1'],
  ] as const)('rewrite %s %s (%s -> %s)', (sdk, value, from, to, toVersion, expected) => {
    expect(rewriteVersionValue(sdk, value, from, to, toVersion)).toBe(expected);
  });

  it('rewrites a TFM keeping its platform suffix', () => {
    expect(rewriteTfm('net10.0-windows', '10.0', '11.0')).toBe('net11.0-windows');
    expect(rewriteTfm('net9.0', '10.0', '11.0')).toBeNull();
  });
});

describe('the platform table (#606 D9, test/fixtures/platform-feeds/platform-table.json)', () => {
  const raw = readFixture('platform-feeds/platform-table.json');

  it('parses the table the bot dispatches', () => {
    const table = parsePlatformTable(raw);
    expect(table.sdks.dotnet!.find((x) => x.channel === '11.0')).toMatchObject({ preview: true, latestVersion: '11.0.0-rc.2', latestSdk: '11.0.100-rc.2.26504.105', lts: false });
    expect(table.sdks.node!.find((x) => x.channel === '26')).toMatchObject({ latestVersion: '26.12.0', preview: false });
    expect(table.sdks.java!.find((x) => x.channel === '25')?.lts).toBe(true);
    expect(table.unstable).toEqual([]);
  });

  it.each([
    ['', /dependency-lookup workflow/],
    ['nope', /JSON channel table/],
    ['{"sdks":[],"unstable":[]}', /sdks must be an object/],
    ['{"sdks":{},"unstable":[1]}', /unstable must be an array of package globs/],
    ['{"sdks":{"node":{}},"unstable":[]}', /sdks.node must be an array/],
    ['{"sdks":{"node":[{"channel":"lts"}]},"unstable":[]}', /channel such as/],
  ])('refuses %j', (input, error) => expect(() => parsePlatformTable(input)).toThrow(error));

  it('a record lists only newer, non-EOL channels, previews flagged', () => {
    const feed = parsePlatformTable(raw).sdks.dotnet!;
    const loc = { sdk: 'dotnet' as const, file: 'a.csproj', kind: 'tfm' as const, value: 'net10.0', channel: '10.0', computed: false, reason: null, line: 0, start: 0, end: 7 };
    const rec = buildPlatformRecord('dotnet', [loc], feed, NOW);
    expect(rec.current).toEqual({ channels: ['10.0'], preview: false });
    expect(rec.candidates.map((c) => c.channel)).toEqual(['11.0']);
    expect(rec.candidates[0].preview).toBe(true);
    expect(buildPlatformRecord('dotnet', [loc], null, NOW)).toMatchObject({ source: 'none', candidates: [] });
  });
});

describe('location scanning on file trees', () => {
  it('reports every location of the S12 docker/actions/platform sample', async () => {
    const found = await scanLocations(directoryFiles(fixture('renovate/docker-actions-platform/input')));
    const by = (sdk: string, kind: string) => found.filter((f) => f.sdk === sdk && f.kind === kind).map((f) => `${f.file}=${f.value}${f.computed ? ' (computed)' : ''}`);
    expect(by('dotnet', 'global.json')).toEqual(['global.json=8.0.100']);
    expect(by('dotnet', 'docker')).toEqual(['Dockerfile=8.0.100', 'Dockerfile=8.0']);
    expect(by('dotnet', 'setup-action')).toEqual(['.github/workflows/ci.yml=8.0.100']);
    expect(by('node', 'version-file').sort()).toEqual(['.node-version=20.11.0', '.nvmrc=20.11.0']);
    // #606 M6: a workflow's `container:` image is a docker location too.
    expect(by('node', 'docker')).toEqual(['.github/workflows/ci.yml=18.19.0', 'Dockerfile=20.11.0-alpine']);
    expect(by('node', 'setup-action')).toEqual(['.github/workflows/ci.yml=20.11.0']);
    expect(by('node', 'tool-versions')).toEqual(['.tool-versions=20.11.0']);
    expect(by('python', 'docker')).toEqual(['Dockerfile=3.11-slim (computed)']);
    expect(by('python', 'setup-action')).toEqual(['.github/workflows/ci.yml=3.11.4']);
    expect(by('java', 'tool-versions')).toEqual(['.tool-versions=temurin-17.0.8+7']);
    expect(by('java', 'setup-action')).toEqual(['.github/workflows/ci.yml=17']);
    expect(by('go', 'setup-action')).toEqual(['.github/workflows/ci.yml=1.21.0']);
  });

  it('TFM in Directory.Build.props vs per csproj, multi-targeting, computed and conditioned TFMs', async () => {
    const root = tempDir();
    writeTree(root, {
      'Directory.Build.props': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
      'src/Multi/Multi.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFrameworks>net10.0;net48</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n',
      'src/Computed/Computed.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>$(NetCurrent)</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
      'src/Cond/Cond.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework Condition="\'$(X)\' == \'1\'">net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
      'src/Ref/Ref.csproj': '<Project>\n  <ItemGroup>\n    <PackageReference Include="Serilog">\n      <Version>3.0.1</Version>\n    </PackageReference>\n  </ItemGroup>\n</Project>\n',
    });
    const found = await scanLocations(directoryFiles(root), ['dotnet']);
    const tfm = found.filter((f) => f.kind === 'tfm').map((f) => [f.file, f.value, f.channel, f.computed]);
    expect(tfm).toEqual([
      ['Directory.Build.props', 'net10.0', '10.0', false],
      ['src/Computed/Computed.csproj', '$(NetCurrent)', null, true],
      ['src/Cond/Cond.csproj', 'net10.0', '10.0', true],
      ['src/Multi/Multi.csproj', 'net10.0;net48', '10.0', false],
    ]);
    expect(found.filter((f) => f.kind === 'package-version')).toEqual([]);
  });

  it('S4: PHP, Ruby and Rust locations in real repositories; ranges and the MSRV reported, never edited (#606 D10/D11)', async () => {
    // Each directory is a repository of its own: a workflow is only one at the root's `.github/workflows/`.
    const root = fixture('platform/s4-php-ruby-rust');
    const found: Awaited<ReturnType<typeof scanLocations>> = [];
    for (const repo of fs.readdirSync(root, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name).sort()) {
      for (const f of await scanLocations(directoryFiles(path.join(root, repo)), ['php', 'ruby', 'rust'])) found.push({ ...f, file: `${repo}/${f.file}` });
    }
    const row = (f: (typeof found)[number]) => `${f.file} ${f.kind}=${f.value} ${f.channel ?? '-'}${notEditedReason(f) === null ? '' : ' (reported)'}`;
    expect(found.filter((f) => f.kind !== 'matrix').map(row).sort()).toEqual([
      'discourse/Gemfile gemfile-ruby=~> 4.0.0 - (reported)',
      'laravel/composer.json composer-require=^8.3 - (reported)',
      'mastodon/.ruby-version version-file=4.0.9 4.0',
      ...Array(3).fill('rubocop/.github/workflows/rubocop.yml setup-action=${{ matrix.ruby }} - (reported)'),
      'rubocop/.github/workflows/rubocop.yml setup-action=2.7 2.7',
      'rubocop/.github/workflows/rubocop.yml setup-action=jruby -',
      'rubocop/.github/workflows/rubocop.yml setup-action=ruby -',
      'rubocop/rubocop.gemspec gemspec-ruby=>= 2.7.0 - (reported)',
      'ruff/Cargo.toml rust-version=1.97 - (reported)',
      'ruff/rust-toolchain.toml rust-toolchain=1.99.0 1.99',
      'symfony-demo/composer.json composer-platform=8.4.1 8.4',
      'symfony-demo/composer.json composer-require=>=8.4 - (reported)',
    ].sort());
    // mastodon's `ruby file: '.ruby-version'` names no version; laravel/framework's local composite action is not setup-php.
    expect(found.some((f) => f.file === 'mastodon/Gemfile' || f.file.startsWith('laravel-framework/'))).toBe(false);
  });

  it('M6: the matrix lists a setup-* step reads, inline and block, on a real workflow (rubocop)', async () => {
    const found = await scanLocations(directoryFiles(fixture('platform/s4-php-ruby-rust/rubocop')), ['ruby']);
    const matrix = found.filter((f) => f.kind === 'matrix').map((f) => `${f.line + 1}:${f.value}=${f.channel ?? '-'}`);
    expect(matrix).toEqual([
      ...['2.7', '3.0', '3.1', '3.2', '3.3', '3.4', '4.0'].map((v) => `28:${v}=${v}`),
      '28:head=-',
      '67:2.7=2.7',
      '68:ruby=-',
      '94:2.7=2.7',
      '95:ruby=-',
    ]);
    // The token span is the value inside its quotes, so an edit keeps them.
    const doc = fs.readFileSync(fixture('platform/s4-php-ruby-rust/rubocop/.github/workflows/rubocop.yml'), 'utf8').split('\n');
    for (const f of found.filter((x) => x.kind === 'matrix')) expect(doc[f.line].replace(/\r$/, '').slice(f.start, f.end)).toBe(f.value);
  });

  it('M6: workflow images (container:, image:, uses: docker://) are docker locations', async () => {
    const root = tempDir();
    writeTree(root, {
      '.github/workflows/ci.yml':
        'jobs:\n  a:\n    container: node:22.11.0\n    services:\n      db:\n        image: postgres:16\n    steps:\n      - uses: docker://node:22-alpine\n      - uses: actions/setup-node@v4\n  b:\n    container:\n      image: "node:20"\n',
    });
    const found = await scanLocations(directoryFiles(root), ['node']);
    expect(found.filter((f) => f.kind === 'docker').map((f) => `${f.line + 1}:${f.value}=${f.channel}`)).toEqual(['3:22.11.0=22', '8:22-alpine=22', '12:20=20']);
    const { read } = await applyOn(
      { '.github/workflows/ci.yml': 'jobs:\n  a:\n    container: node:22.11.0\n    strategy:\n      matrix:\n        node: [20, 22]\n    steps:\n      - uses: actions/setup-node@v4\n        with:\n          node-version: ${{ matrix.node }}\n' },
      { sdk: 'node', fromChannel: '22', toChannel: '24', toVersion: '24.11.1' },
    );
    expect(read('.github/workflows/ci.yml')).toBe(
      'jobs:\n  a:\n    container: node:24.11.1\n    strategy:\n      matrix:\n        node: [20, 24]\n    steps:\n      - uses: actions/setup-node@v4\n        with:\n          node-version: ${{ matrix.node }}\n',
    );
  });
});

async function applyOn(files: Record<string, string>, plan: Parameters<typeof applyPlatform>[1], options?: Parameters<typeof applyPlatform>[2]) {
  const root = tempDir();
  writeTree(root, files);
  const result = await applyPlatform(directoryFiles(root), plan, options);
  const read = (f: string) => fs.readFileSync(path.join(root, f), 'utf8');
  return { result, read };
}

describe('applyPlatform', () => {
  const dotnet = { sdk: 'dotnet' as const, fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' };

  it('strategy replace (default): every location on the channel, nothing else', async () => {
    const { result, read } = await applyOn(
      {
        'Directory.Build.props': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
        'old/Old.csproj': '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>\n',
        'global.json': '{\n  "sdk": {\n    "version": "10.0.100",\n    "rollForward": "latestFeature"\n  }\n}\n',
        Dockerfile: 'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine\n',
        '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-dotnet@v5\n        with:\n          dotnet-version: 10.0.x\n',
      },
      dotnet,
    );
    expect(result.edited).toEqual(['.github/workflows/ci.yml', 'Directory.Build.props', 'Dockerfile', 'global.json']);
    expect(read('Directory.Build.props')).toContain('<TargetFramework>net11.0</TargetFramework>');
    expect(read('old/Old.csproj')).toContain('net9.0');
    expect(read('global.json')).toBe('{\n  "sdk": {\n    "version": "11.0.100",\n    "rollForward": "latestFeature"\n  }\n}\n');
    expect(read('Dockerfile')).toBe('FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine\n');
    expect(read('.github/workflows/ci.yml')).toContain('dotnet-version: 11.0.x');
  });

  it('strategy add + keepPrevious on a multi-target project; TargetFramework becomes TargetFrameworks', async () => {
    expect(nextTfmList('net10.0;net48', { ...dotnet, strategy: 'add' })).toBe('net11.0;net10.0;net48');
    expect(nextTfmList('net9.0;net10.0', { ...dotnet, strategy: 'add', keepPrevious: 1 })).toBe('net11.0;net10.0');
    expect(nextTfmList('net9.0;net10.0', { ...dotnet, strategy: 'add', keepPrevious: 2 })).toBe('net11.0;net10.0;net9.0');
    expect(nextTfmList('net10.0;net48', dotnet)).toBe('net11.0;net48');
    const { read } = await applyOn({ 'A.csproj': '<Project>\r\n  <PropertyGroup>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n' }, { ...dotnet, strategy: 'add' });
    expect(read('A.csproj')).toBe('<Project>\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net11.0;net10.0</TargetFrameworks>\r\n  </PropertyGroup>\r\n</Project>\r\n');
  });

  it('a computed or conditioned TFM is reported, never edited (R8)', async () => {
    const files = {
      'C.csproj': '<Project><PropertyGroup><TargetFramework>$(NetCurrent)</TargetFramework></PropertyGroup></Project>\n',
      'D.csproj': '<Project><PropertyGroup><TargetFramework Condition="true">net10.0</TargetFramework></PropertyGroup></Project>\n',
    };
    const { result, read } = await applyOn(files, dotnet);
    expect(result.edited).toEqual([]);
    expect(result.reported.map((r) => r.file).sort()).toEqual(['C.csproj', 'D.csproj']);
    expect(read('C.csproj')).toBe(files['C.csproj']);
  });

  it('S5: a condition that tests the TFM for the old channel is reported beside the move, never edited', async () => {
    const project =
      '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n' +
      '  <PropertyGroup Condition="\'$(TargetFramework)\' == \'net10.0\'">\n    <DefineConstants>NET10</DefineConstants>\n  </PropertyGroup>\n' +
      '  <ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible(\'$(TargetFramework)\', \'net10.0\'))">\n  </ItemGroup>\n</Project>\n';
    const { result, read } = await applyOn({ 'A.csproj': project }, dotnet);
    expect(result.edited).toEqual(['A.csproj']);
    expect(result.reported).toHaveLength(1);
    expect(result.reported[0]).toMatchObject({ file: 'A.csproj', value: "'$(TargetFramework)' == 'net10.0'" });
    expect(result.reported[0].reason).toContain('PropertyGroup condition');
    expect(read('A.csproj')).toBe(project.replace('<TargetFramework>net10.0<', '<TargetFramework>net11.0<'));
  });

  it('lockPackageMajor moves the project version major only when opted in', async () => {
    const files = { 'Directory.Build.props': '<Project><PropertyGroup>\n<TargetFramework>net10.0</TargetFramework>\n<Version>10.0.0-preview.84</Version>\n</PropertyGroup></Project>\n' };
    expect((await applyOn(files, dotnet)).read('Directory.Build.props')).toContain('<Version>10.0.0-preview.84</Version>');
    expect((await applyOn(files, { ...dotnet, lockPackageMajor: true })).read('Directory.Build.props')).toContain('<Version>11.0.0-preview.84</Version>');
  });

  describe('older channels (platform-locations-PRD P6, P7)', () => {
    // Dotnet.Templates, 2026-11-13: a sdk:9.0 build stage under a net10.0 repository stayed behind, unseen.
    const files = {
      'A.csproj': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
      'old/Old.csproj': '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>\n',
      Dockerfile: 'FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\n',
      '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-dotnet@v5\n        with:\n          dotnet-version: 9.0.x\n',
    };
    const older = "on 9.0, older than the repository's 10.0";

    it('auto: an older Docker stage and setup pin follow the move; an older TFM is reported', async () => {
      const { result, read } = await applyOn(files, dotnet);
      expect(read('Dockerfile')).toBe('FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:11.0\n');
      expect(read('.github/workflows/ci.yml')).toContain('dotnet-version: 11.0.x');
      expect(read('old/Old.csproj')).toBe(files['old/Old.csproj']);
      expect(result.reported).toContainEqual({ file: 'old/Old.csproj', value: 'net9.0', reason: older });
    });

    it('report: nothing older moves, and each is listed', async () => {
      const { result, read } = await applyOn(files, { ...dotnet, lagging: 'report' });
      expect(read('Dockerfile')).toBe('FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:11.0\n');
      expect(result.reported).toContainEqual({ file: 'Dockerfile', value: '9.0', reason: older });
      expect(result.reported).toContainEqual({ file: '.github/workflows/ci.yml', value: '9.0.x', reason: older });
    });

    it('a move inside the channel leaves older locations, listed', async () => {
      const { result, read } = await applyOn(files, { ...dotnet, toChannel: '10.0', toVersion: '10.0.5', sdkVersion: '10.0.105' });
      expect(read('Dockerfile')).toContain('sdk:9.0 AS build');
      expect(result.reported).toContainEqual({ file: 'Dockerfile', value: '9.0', reason: older });
    });

    it('P7: a Docker tag the registry does not have is reported, not written', async () => {
      const seen: string[] = [];
      const tagExists = async (image: string, tag: string) => {
        seen.push(`${image}:${tag}`);
        return tag !== '11.0' || image.endsWith('/aspnet');
      };
      const { result, read } = await applyOn(files, dotnet, { tagExists });
      expect(seen).toEqual(['mcr.microsoft.com/dotnet/sdk:11.0', 'mcr.microsoft.com/dotnet/aspnet:11.0']);
      expect(read('Dockerfile')).toBe('FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:11.0\n');
      expect(result.reported).toContainEqual({ file: 'Dockerfile', value: '9.0', reason: 'mcr.microsoft.com/dotnet/sdk:11.0 does not exist in its registry' });
    });
  });

  it('honours locations / exclude overrides', async () => {
    const files = {
      'a/A.csproj': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
      'b/B.csproj': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
    };
    expect((await applyOn(files, { ...dotnet, exclude: ['b/**'] })).result.edited).toEqual(['a/A.csproj']);
    expect((await applyOn(files, { ...dotnet, locations: ['b/*.csproj'] })).result.edited).toEqual(['b/B.csproj']);
  });

  it('node: version files, setup-node, Docker; ranges and @types/node are reported', async () => {
    const { result, read } = await applyOn(
      {
        '.nvmrc': '20\n',
        '.node-version': '20.11.0\n',
        'package.json': '{\n  "engines": { "node": ">=20" },\n  "devDependencies": { "@types/node": "^20.11.0" }\n}\n',
        'web/package.json': '{\n  "engines": {\n    "node": "^18 || ^20"\n  }\n}\n',
        Dockerfile: 'FROM node:20-alpine\n',
        '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v5\n        with:\n          node-version: \'20.x\'\n',
      },
      { sdk: 'node', fromChannel: '20', toChannel: '22', toVersion: '22.21.0' },
    );
    expect(read('.nvmrc')).toBe('22\n');
    expect(read('.node-version')).toBe('22.21.0\n');
    expect(read('package.json')).toContain('"node": ">=22"');
    expect(read('package.json')).toContain('"@types/node": "^20.11.0"');
    expect(read('Dockerfile')).toBe('FROM node:22-alpine\n');
    expect(read('.github/workflows/ci.yml')).toContain("node-version: '22.x'");
    expect(result.reported.map((r) => r.file).sort()).toEqual(['package.json', 'web/package.json']);
  });

  it('python, java and go: version files, setup-*, images and go.mod', async () => {
    const py = await applyOn({ '.python-version': '3.12\n', Dockerfile: 'FROM python:3.12-slim\n', 'pyproject.toml': 'requires-python = ">=3.12"\n' }, { sdk: 'python', fromChannel: '3.12', toChannel: '3.13' });
    expect(py.read('.python-version')).toBe('3.13\n');
    expect(py.read('Dockerfile')).toBe('FROM python:3.13-slim\n');
    expect(py.result.reported).toEqual([expect.objectContaining({ file: 'pyproject.toml' })]);
    const java = await applyOn({ 'pom.xml': '<properties>\n  <maven.compiler.release>21</maven.compiler.release>\n</properties>\n', Dockerfile: 'FROM eclipse-temurin:21-jre\n' }, { sdk: 'java', fromChannel: '21', toChannel: '25' });
    expect(java.read('pom.xml')).toContain('<maven.compiler.release>25</maven.compiler.release>');
    expect(java.read('Dockerfile')).toBe('FROM eclipse-temurin:25-jre\n');
    const go = await applyOn({ 'go.mod': 'module x\n\ngo 1.24\n\ntoolchain go1.24.3\n', Dockerfile: 'FROM golang:1.24-alpine\n' }, { sdk: 'go', fromChannel: '1.24', toChannel: '1.25', toVersion: '1.25.2' });
    expect(go.read('go.mod')).toBe('module x\n\ngo 1.25\n\ntoolchain go1.25.2\n');
    expect(go.read('Dockerfile')).toBe('FROM golang:1.25-alpine\n');
  });

  it('php, ruby and rust (#606 D10/D11): exact pins move, ranges and the MSRV are reported', async () => {
    const php = await applyOn(
      { 'composer.json': '{\n  "require": {\n    "php": ">=8.4"\n  },\n  "config": {\n    "platform": {\n      "php": "8.4.1"\n    }\n  }\n}\n', Dockerfile: 'FROM php:8.4-fpm\n' },
      { sdk: 'php', fromChannel: '8.4', toChannel: '8.5', toVersion: '8.5.1' },
    );
    expect(php.read('composer.json')).toContain('"php": ">=8.4"');
    expect(php.read('composer.json')).toContain('"php": "8.5.1"');
    expect(php.read('Dockerfile')).toBe('FROM php:8.5-fpm\n');
    expect(php.result.reported).toEqual([expect.objectContaining({ file: 'composer.json' })]);

    const ruby = await applyOn(
      { '.ruby-version': '3.4.7\n', Gemfile: 'source "https://rubygems.org"\nruby "3.4.7"\n', 'x.gemspec': 'Gem::Specification.new do |s|\n  s.required_ruby_version = ">= 3.2"\nend\n' },
      { sdk: 'ruby', fromChannel: '3.4', toChannel: '4.0', toVersion: '4.0.1' },
    );
    expect(ruby.read('.ruby-version')).toBe('4.0.1\n');
    expect(ruby.read('Gemfile')).toBe('source "https://rubygems.org"\nruby "4.0.1"\n');
    expect(ruby.result.reported).toEqual([expect.objectContaining({ file: 'x.gemspec' })]);

    const rust = await applyOn(
      { 'rust-toolchain.toml': '[toolchain]\nchannel = "1.99.0"\n', 'Cargo.toml': '[package]\nrust-version = "1.97"\n' },
      { sdk: 'rust', fromChannel: '1.99', toChannel: '1.100', toVersion: '1.100.0' },
    );
    expect(rust.read('rust-toolchain.toml')).toBe('[toolchain]\nchannel = "1.100.0"\n');
    expect(rust.read('Cargo.toml')).toBe('[package]\nrust-version = "1.97"\n');

    // Preview = the beta channel: the pinned toolchain moves to the dated beta the plan names.
    const beta = await applyOn(
      { 'rust-toolchain.toml': '[toolchain]\nchannel = "1.99.0"\n' },
      { sdk: 'rust', fromChannel: '1.99', toChannel: '1.100', toVersion: '1.100.0-beta.13', sdkVersion: 'beta-2026-11-08' },
    );
    expect(beta.read('rust-toolchain.toml')).toBe('[toolchain]\nchannel = "beta-2026-11-08"\n');
  });
});

/**
 * S19 golden: the dotnet adapter's dry run on this repo BEFORE 9808c1a3b ("Retarget the whole solution
 * to .NET 11") against that hand-made commit. Every edit we make is in the commit; every commit line we
 * do not make is in an explained category (SOURCE.md).
 */
describe('S19 golden: net10 -> net11 on this repository', () => {
  const OWN = /<TargetFrameworks?>|<Version>\d|"version": "|^FROM mcr\.microsoft\.com\/dotnet\/|dotnet-version:/;

  it('matches the hand-made migration commit, differences explained', async () => {
    const root = tempDir();
    copyTree(fixture('platform/s19-net11/before'), root);
    const files = directoryFiles(root);
    const before = new Map<string, string>();
    for (const f of await files.list()) before.set(f, fs.readFileSync(path.join(root, f), 'utf8'));
    const result = await applyPlatform(files, { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', sdkVersion: '11.0.100-rc.1.26425.128', lockPackageMajor: true });

    const commit = readFixture('platform/s19-net11/commit.diff');
    const ours: Array<LinePair & { file: string }> = [];
    for (const [f, text] of before) {
      const after = fs.readFileSync(path.join(root, f), 'utf8');
      const a = text.split(/\r?\n/);
      const b = after.split(/\r?\n/);
      expect(b.length).toBe(a.length);
      a.forEach((l, i) => {
        if (l !== b[i]) ours.push({ file: f, minus: l, plus: b[i] });
      });
    }
    const theirs = [...before.keys()].flatMap((f) => {
      try {
        return diffPairsLoose(commit, f).map((p) => ({ file: f, ...p }));
      } catch {
        return [];
      }
    });
    const key = (p: { file: string; minus: string; plus: string }) => `${p.file}\n${p.minus.replace(/^\ufeff/, '')}\n${p.plus.replace(/^\ufeff/, '')}`;
    const theirKeys = new Set(theirs.map(key));
    // 1. Nothing we change is outside the human commit.
    expect(ours.filter((p) => !theirKeys.has(key(p)))).toEqual([]);
    // 2. Every platform line of the human commit, we change too.
    const ourKeys = new Set(ours.map(key));
    const missing = theirs.filter((p) => OWN.test(p.minus.trim()) && !ourKeys.has(key(p)));
    expect(missing).toEqual([]);
    // 3. The rest is the explained categories: PackageReference bumps/removals (locked packages, a
    //    cleanup) and workflow prose.
    const rest = theirs.filter((p) => !ourKeys.has(key(p)));
    for (const p of rest) expect(/PackageReference|^\s*#|net1[01]\.0 project/.test(p.minus) || p.minus === '').toBe(true);
    expect(result.edited.length).toBeGreaterThan(30);
  });
});

/** Like diffPairs, but tolerates hunks whose -/+ counts differ (removed PackageReference lines). */
function diffPairsLoose(diff: string, file: string): LinePair[] {
  const sections = diff.split(/^diff --git /m).filter(Boolean);
  const section = sections.find((s) => s.startsWith(`a/${file} b/${file}`));
  if (!section) throw new Error('no section');
  const out: LinePair[] = [];
  let minus: string[] = [];
  let plus: string[] = [];
  const flush = () => {
    const n = Math.max(minus.length, plus.length);
    for (let i = 0; i < n; i++) out.push({ minus: minus[i] ?? '', plus: plus[i] ?? '' });
    minus = [];
    plus = [];
  };
  for (const line of section.split('\n')) {
    if (line.startsWith('---') || line.startsWith('+++')) continue;
    if (line.startsWith('-')) minus.push(line.slice(1).replace(/\r$/, ''));
    else if (line.startsWith('+')) plus.push(line.slice(1).replace(/\r$/, ''));
    else flush();
  }
  flush();
  return out;
}

void diffPairs;

describe('M4: computed TFMs followed to their definition (platform-locations-PRD R4, P9)', () => {
  // MintPlayer.Dotnet.Templates, read 2026-11-13: *.csproj.in -> GeneratedContentProperties -> eng/Versions.props.
  const versionsProps = '<Project>\n  <PropertyGroup>\n    <AspNetCoreMajorVersion>10</AspNetCoreMajorVersion>\n    <AspNetCoreMinorVersion>0</AspNetCoreMinorVersion>\n'
    + '    <AspNetCoreMajorMinorVersion>$(AspNetCoreMajorVersion).$(AspNetCoreMinorVersion)</AspNetCoreMajorMinorVersion>\n'
    + '    <DefaultNetCoreTargetFramework>net$(AspNetCoreMajorMinorVersion)</DefaultNetCoreTargetFramework>\n  </PropertyGroup>\n</Project>\n';
  const templates = {
    'eng/Versions.props': versionsProps,
    'src/Templates/content/Web/Web.csproj.in': '<Project Sdk="Microsoft.NET.Sdk.Web">\n  <PropertyGroup>\n    <TargetFramework>${DefaultNetCoreTargetFramework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
    'src/Lib/Lib.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>$(DefaultNetCoreTargetFramework)</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
  };
  const dotnet11 = { sdk: 'dotnet' as const, fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' };

  async function scan(files: Record<string, string>) {
    const root = tempDir();
    writeTree(root, files);
    return (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.kind === 'tfm' || f.kind === 'tfm-property');
  }

  it('the Templates chain resolves to the one token that carries the major, once for every project that reads it', async () => {
    const found = await scan(templates);
    expect(found.map((f) => `${f.file}:${f.line + 1} ${f.kind}=${f.value} ${f.channel}${f.computed ? ' (computed)' : ''}`)).toEqual([
      'eng/Versions.props:3 tfm-property=10 10.0',
    ]);
    const f = found[0];
    expect(versionsProps.split('\n')[f.line].slice(f.start, f.end)).toBe('10');
  });

  it('the move edits the defining line only', async () => {
    const { result, read } = await applyOn(templates, dotnet11);
    expect(result).toEqual({ edited: ['eng/Versions.props'], reported: [] });
    expect(read('eng/Versions.props')).toBe(versionsProps.replace('<AspNetCoreMajorVersion>10<', '<AspNetCoreMajorVersion>11<'));
    expect(read('src/Lib/Lib.csproj')).toBe(templates['src/Lib/Lib.csproj']);
  });

  it('a property holding the whole TFM moves its channel', async () => {
    const files = {
      'Directory.Build.props': '<Project>\n  <PropertyGroup>\n    <NetCurrent>net10.0</NetCurrent>\n  </PropertyGroup>\n</Project>\n',
      'src/App/App.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>$(NetCurrent)</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
    };
    expect((await scan(files)).map((f) => `${f.kind}=${f.value} ${f.channel}`)).toEqual(['tfm-property=10.0 10.0']);
    const { read } = await applyOn(files, dotnet11);
    expect(read('Directory.Build.props')).toContain('<NetCurrent>net11.0</NetCurrent>');
  });

  it.each([
    ['defined twice', { 'a.props': '<Project><PropertyGroup>\n<NetCurrent>net10.0</NetCurrent>\n</PropertyGroup></Project>\n', 'b.targets': '<Project><PropertyGroup>\n<NetCurrent>net9.0</NetCurrent>\n</PropertyGroup></Project>\n' }, '$(NetCurrent) is defined 2 times'],
    ['conditioned group', { 'a.props': '<Project><PropertyGroup Condition="\'$(X)\' == \'1\'">\n<NetCurrent>net10.0</NetCurrent>\n</PropertyGroup></Project>\n' }, '$(NetCurrent) is conditioned'],
    ['conditioned element', { 'a.props': '<Project><PropertyGroup>\n<NetCurrent Condition="\'$(X)\' == \'\'">net10.0</NetCurrent>\n</PropertyGroup></Project>\n' }, '$(NetCurrent) is conditioned'],
    ['undefined', {}, '$(NetCurrent) is not defined in a .props or .targets file'],
    ['not a TFM', { 'a.props': '<Project><PropertyGroup>\n<NetCurrent>netstandard2.0</NetCurrent>\n</PropertyGroup></Project>\n' }, "evaluates to 'netstandard2.0'"],
  ])('a chain that does not resolve plainly (%s) stays a reported computed TFM, and says why', async (_, props, why) => {
    const files = { ...props, 'src/App/App.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>$(NetCurrent)</TargetFramework>\n  </PropertyGroup>\n</Project>\n' };
    const found = await scan(files);
    expect(found.filter((f) => f.kind === 'tfm-property')).toEqual([]);
    const tfm = found.find((f) => f.file === 'src/App/App.csproj')!;
    expect(tfm.computed).toBe(true);
    expect(tfm.reason).toContain(why);
  });

  it('a cycle is refused, not followed forever', async () => {
    const files = {
      'a.props': '<Project><PropertyGroup>\n<A>$(B)</A>\n<B>$(A)</B>\n</PropertyGroup></Project>\n',
      'src/App/App.csproj': '<Project><PropertyGroup><TargetFramework>$(A)</TargetFramework></PropertyGroup></Project>\n',
    };
    expect((await scan(files)).find((f) => f.file === 'src/App/App.csproj')!.reason).toContain('cycle');
  });

  it('strategy add cannot multi-target a property: it is reported', async () => {
    const { result, read } = await applyOn(templates, { ...dotnet11, strategy: 'add' });
    expect(read('eng/Versions.props')).toBe(versionsProps);
    expect(result.reported).toContainEqual(expect.objectContaining({ file: 'eng/Versions.props', value: '10' }));
  });
});

describe('M5: template.json follows the TFM (platform-locations-PRD R2, P8)', () => {
  const templateJson = '{\n  "symbols": {\n    "Framework": {\n      "type": "parameter",\n      "description": "The target framework for the project.",\n'
    + '      "datatype": "choice",\n      "choices": [\n        {\n          "choice": "net10.0",\n          "description": "Target net10.0"\n        }\n      ],\n'
    + '      "replaces": "net10.0",\n      "defaultValue": "net10.0"\n    },\n    "Name": { "type": "parameter", "description": "net10.0 is not a framework symbol here" }\n  }\n}\n';
  const project = '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n';
  const dotnet11 = { sdk: 'dotnet' as const, fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' };
  const path = 'content/Web/.template.config/template.json';

  it('the framework choice, its description, replaces and defaultValue are locations; other text is not', async () => {
    const root = tempDir();
    writeTree(root, { [path]: templateJson });
    const found = (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.kind === 'template-framework');
    expect(found.map((f) => `${f.line + 1}:${f.value}=${f.channel}`)).toEqual(['9:net10.0=10.0', '10:net10.0=10.0', '13:net10.0=10.0', '14:net10.0=10.0']);
    for (const f of found) expect(templateJson.split('\n')[f.line].slice(f.start, f.end)).toBe('net10.0');
  });

  it('moves with the template\'s project TFM', async () => {
    const { result, read } = await applyOn({ [path]: templateJson, 'content/Web/Web.csproj': project }, dotnet11);
    expect(result.edited).toEqual(['content/Web/.template.config/template.json', 'content/Web/Web.csproj']);
    expect(read(path)).toBe(templateJson.replace(/"(choice|replaces|defaultValue)": "net10\.0"/g, '"$1": "net11.0"').replace('Target net10.0', 'Target net11.0'));
  });

  it('without a TFM moving in the same pull request it is reported, never moved alone', async () => {
    const { result, read } = await applyOn({ [path]: templateJson }, dotnet11);
    expect(read(path)).toBe(templateJson);
    expect(result.reported).toContainEqual({ file: path, value: 'net10.0', reason: 'a template framework moves only with a TFM this move changes, and none changes' });
  });

  it('moves when its project template\'s TFM moves through a property (the Dotnet.Templates shape)', async () => {
    const files = {
      [path]: templateJson,
      'eng/Versions.props': '<Project>\n  <PropertyGroup>\n    <Major>10</Major>\n    <Tfm>net$(Major).0</Tfm>\n  </PropertyGroup>\n</Project>\n',
      'content/Web/Web.csproj.in': '<Project>\n  <PropertyGroup>\n    <TargetFramework>${Tfm}</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
    };
    const { result } = await applyOn(files, dotnet11);
    expect(result.edited).toEqual(['content/Web/.template.config/template.json', 'eng/Versions.props']);
    // A property read only by projects outside the template does not move it.
    const { ['content/Web/Web.csproj.in']: project, ...rest } = files;
    const elsewhere = { ...rest, 'src/Lib/Lib.csproj': project };
    expect((await applyOn(elsewhere, dotnet11)).result.edited).toEqual(['eng/Versions.props']);
  });

  it('strategy add moves replaces and defaultValue, and reports the choice to add by hand', async () => {
    const { result, read } = await applyOn({ [path]: templateJson, 'content/Web/Web.csproj': project }, { ...dotnet11, strategy: 'add' });
    expect(read(path)).toBe(templateJson.replace(/"(replaces|defaultValue)": "net10\.0"/g, '"$1": "net11.0"'));
    expect(result.reported.filter((r) => r.file === path)).toEqual([
      { file: path, value: 'net10.0', reason: 'strategy add: add the net11.0 choice by hand' },
      { file: path, value: 'net10.0', reason: 'strategy add: add the net11.0 choice by hand' },
    ]);
  });
});

describe('S4 golden: MintPlayer.Dotnet.Templates (platform-locations-PRD §6)', () => {
  const pkg = 'src/MintPlayer.Dotnet.Templates';
  const template = `${pkg}/content/WebApplication/.template.config/template.json`;
  const dockerfile = `${pkg}/content/WebApplication/Web/Dockerfile`;

  function copy() {
    const root = tempDir();
    copyTree(fixture('platform/dotnet-templates/before'), root);
    return root;
  }

  it('finds every location the next .NET move must change', async () => {
    const found = await scanLocations(directoryFiles(copy()), ['dotnet']);
    expect(found.map((f) => `${f.file}:${f.line + 1} ${f.kind}=${f.value} ${f.channel}${f.computed ? ' (computed)' : ''}`).sort()).toEqual([
      '.github/workflows/publish-master.yml:27 setup-action=9.0.101 9.0',
      `${pkg}/MintPlayer.Dotnet.Templates.csproj:10 package-version=10.0.0 10.0`,
      `${pkg}/MintPlayer.Dotnet.Templates.csproj:4 tfm=net10.0 10.0`,
      `${template}:44 template-framework=net10.0 10.0`,
      `${template}:45 template-framework=net10.0 10.0`,
      `${template}:48 template-framework=net10.0 10.0`,
      `${template}:49 template-framework=net10.0 10.0`,
      `${dockerfile}:10 docker=9.0 9.0`,
      `${dockerfile}:26 docker=9.0 9.0`,
      `${pkg}/eng/Versions.props:3 tfm-property=10 10.0`,
    ]);
    // The four project templates resolve to the one property (S4: `Label` on its PropertyGroup is not a condition).
    expect(found.find((f) => f.kind === 'tfm-property')!.readers).toHaveLength(4);
  });

  it('net10.0 -> net11.0 moves the chain, the package, template.json and the older stages, as §6 expects', async () => {
    const root = copy();
    const result = await applyPlatform(directoryFiles(root), { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' }, { tagExists: async () => true });
    const read = (f: string) => fs.readFileSync(path.join(root, f), 'utf8');
    expect(result.edited.sort()).toEqual([
      '.github/workflows/publish-master.yml',
      `${pkg}/MintPlayer.Dotnet.Templates.csproj`,
      dockerfile,
      template,
      `${pkg}/eng/Versions.props`,
    ].sort());
    expect(result.reported).toEqual([]);
    expect(read(`${pkg}/eng/Versions.props`)).toContain('<AspNetCoreMajorVersion>11</AspNetCoreMajorVersion>');
    expect(read(template).match(/net1\d\.0/g)).toEqual(['net11.0', 'net11.0', 'net11.0', 'net11.0']);
    expect(read(dockerfile)).toContain('FROM mcr.microsoft.com/dotnet/sdk:11.0 AS dotnet-build');
    expect(read(dockerfile)).toContain('FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final');
    expect(read(dockerfile)).toContain('FROM node:22-alpine AS node-build');
  });
});

describe('M6: Node runtimes (platform-locations-PRD R8, P10)', () => {
  const runtimes = ['node20', 'node24'];
  const node24 = { sdk: 'node' as const, fromChannel: '22', toChannel: '24', toVersion: '24.11.1', actionRuntimes: runtimes };
  const node26 = { sdk: 'node' as const, fromChannel: '24', toChannel: '26', toVersion: '26.0.0', actionRuntimes: runtimes };
  const action = (using: string) => `name: Cherry pick\nruns:\n  using: '${using}'\n  main: dist/index.js\n`;

  async function scan(files: Record<string, string>) {
    const root = tempDir();
    writeTree(root, files);
    return scanLocations(directoryFiles(root), ['node']);
  }

  it('runs.using is an action-runtime location; one under .github/ is reported, since the bot never writes there', async () => {
    const found = await scan({ 'cherry-pick/action.yml': action('node16'), '.github/actions/x/action.yaml': action('node20') });
    expect(found.map((f) => `${f.file}:${f.line + 1} ${f.kind}=${f.value} ${f.channel}`)).toEqual([
      '.github/actions/x/action.yaml:3 action-runtime=node20 20',
      'cherry-pick/action.yml:3 action-runtime=node16 16',
    ]);
    expect(notEditedReason(found[0])).toBe('under .github/, which the bot\'s pull requests never change');
  });

  it('moves to the highest runtime GitHub runs that is not above the target, older actions included', async () => {
    const { result, read } = await applyOn({ 'cherry-pick/action.yml': action('node16'), 'delay/action.yml': action('node20'), '.nvmrc': '22\n' }, node24);
    expect(read('cherry-pick/action.yml')).toBe(action('node24'));
    expect(read('delay/action.yml')).toBe(action('node24'));
    expect(result.reported).toEqual([]);
  });

  it('a target GitHub does not run yet leaves the highest it runs, and says so', async () => {
    const { result, read } = await applyOn({ 'a/action.yml': action('node20'), '.nvmrc': '24\n' }, node26);
    expect(read('a/action.yml')).toBe(action('node24'));
    expect(result.reported).toContainEqual({ file: 'a/action.yml', value: 'node20', reason: 'GitHub runs node24 at most for actions; node26 is not offered yet' });
  });

  it('without the runtime table it is reported, never guessed', async () => {
    const { result, read } = await applyOn({ 'a/action.yml': action('node22'), '.nvmrc': '22\n' }, { ...node24, actionRuntimes: undefined });
    expect(read('a/action.yml')).toBe(action('node22'));
    expect(result.reported).toContainEqual({ file: 'a/action.yml', value: 'node22', reason: 'the plan names no Node runtimes GitHub runs (actionRuntimes)' });
  });

  it('a NodeSource setup script in a Dockerfile RUN moves', async () => {
    const dockerfile = 'FROM mcr.microsoft.com/dotnet/sdk:10.0\nRUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \\\n    && apt-get install -y nodejs\n';
    const found = (await scan({ Dockerfile: dockerfile })).filter((f) => f.sdk === 'node');
    expect(found.map((f) => `${f.line + 1} ${f.kind}=${f.value} ${f.channel}`)).toEqual(['2 docker=22 22']);
    const { read } = await applyOn({ Dockerfile: dockerfile }, node24);
    expect(read('Dockerfile')).toBe(dockerfile.replace('setup_22.x', 'setup_24.x'));
  });

  it('an ARG default that feeds a FROM tag is the location, and the FROM is no longer computed', async () => {
    const dockerfile = 'ARG NODE_VERSION=22\nFROM node:${NODE_VERSION}-alpine AS build\nFROM node:${NODE_VERSION}-alpine\n';
    const found = await scan({ Dockerfile: dockerfile });
    expect(found.map((f) => `${f.line + 1} ${f.kind}=${f.value} ${f.channel}${f.computed ? ' (computed)' : ''}`)).toEqual(['1 docker=22 22']);
    const checked: string[] = [];
    const { read } = await applyOn({ Dockerfile: dockerfile }, node24, { tagExists: async (image, tag) => { checked.push(`${image}:${tag}`); return true; } });
    expect(read('Dockerfile')).toBe(dockerfile.replace('NODE_VERSION=22', 'NODE_VERSION=24'));
    expect(checked).toEqual(['node:24-alpine']);
  });

  it('an ARG defined twice, or without a default, keeps the FROM computed', async () => {
    const twice = await scan({ Dockerfile: 'ARG NODE_VERSION=22\nARG NODE_VERSION=20\nFROM node:${NODE_VERSION}\n' });
    expect(twice.map((f) => `${f.kind}=${f.value} ${f.computed}`)).toEqual(['docker=${NODE_VERSION} true']);
    const bare = await scan({ Dockerfile: 'ARG NODE_VERSION\nFROM node:$NODE_VERSION\n' });
    expect(bare.map((f) => `${f.kind}=${f.value} ${f.computed}`)).toEqual(['docker=$NODE_VERSION true']);
  });
});

describe('M3: multi-line setup inputs (platform-locations-PRD R7) and the package\'s own <PackageVersion> (R5)', () => {
  // DotnetDesktop.Tools' _build.yml: two SDKs installed side by side, one deliberate (10.0.x), one stale (rc.1).
  const twoSdks = 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-dotnet@v5\n        with:\n          dotnet-version: |\n            10.0.x\n            11.0.100-rc.1.26425.128\n      - run: dotnet build\n';

  it('each line of a `dotnet-version: |` block is its own setup-action location, with its own channel', async () => {
    const root = tempDir();
    writeTree(root, { '.github/workflows/_build.yml': twoSdks });
    const found = (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.kind === 'setup-action');
    expect(found.map((f) => `${f.line + 1}:${f.value}=${f.channel}${f.computed ? ' (computed)' : ''}`)).toEqual([
      '7:10.0.x=10.0',
      '8:11.0.100-rc.1.26425.128=11.0',
    ]);
    const doc = twoSdks.split('\n');
    for (const f of found) expect(doc[f.line].slice(f.start, f.end)).toBe(f.value);
  });

  it('an in-channel move edits its own line of the block; the older listed SDK stays and is reported', async () => {
    const { result, read } = await applyOn({ '.github/workflows/_build.yml': twoSdks },
      { sdk: 'dotnet', fromChannel: '11.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' });
    expect(read('.github/workflows/_build.yml')).toBe(twoSdks.replace('11.0.100-rc.1.26425.128', '11.0.100'));
    expect(result.reported).toContainEqual(expect.objectContaining({ file: '.github/workflows/_build.yml', value: '10.0.x' }));
  });

  it('under lagging auto an older SDK of a list does not follow a channel move (it is deliberate, like a matrix entry)', async () => {
    const block = twoSdks.replace('11.0.100-rc.1.26425.128', '11.0.x');
    const { result, read } = await applyOn({ '.github/workflows/_build.yml': block }, { sdk: 'dotnet', fromChannel: '11.0', toChannel: '12.0', toVersion: '12.0.0', sdkVersion: '12.0.100' });
    expect(read('.github/workflows/_build.yml')).toBe(block.replace('11.0.x', '12.0.x'));
    expect(result.reported).toContainEqual({ file: '.github/workflows/_build.yml', value: '10.0.x', reason: "on 10.0, older than the repository's 11.0" });
  });

  it('a plain <PackageVersion> is the package\'s own version; a CPM <PackageVersion Include> stays a dependency', async () => {
    const files = {
      'src/T/T.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <PackageVersion>10.0.0</PackageVersion>\n  </PropertyGroup>\n</Project>\n',
      'Directory.Packages.props': '<Project>\n  <ItemGroup>\n    <PackageVersion Include="Serilog" Version="10.1.0" />\n  </ItemGroup>\n</Project>\n',
    };
    const root = tempDir();
    writeTree(root, files);
    const own = (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.kind === 'package-version');
    expect(own.map((f) => `${f.file}=${f.value}`)).toEqual(['src/T/T.csproj=10.0.0']);

    const { read } = await applyOn(files, { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100', lockPackageMajor: true });
    expect(read('src/T/T.csproj')).toContain('<PackageVersion>11.0.0</PackageVersion>');
    expect(read('Directory.Packages.props')).toBe(files['Directory.Packages.props']);
  });
});
