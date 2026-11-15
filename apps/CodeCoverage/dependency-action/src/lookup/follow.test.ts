import * as fs from 'node:fs';
import * as path from 'node:path';
import type { LookupDocument, LookupRecord } from '../contracts/types';
import { copyTree, fixture, readJsonFixture, tempDir } from '../testing/helpers';
import { directoryFiles } from '../util/repoFiles';
import { followerRecords, followerVersion, type FollowRule } from './follow';
import { mapReport, type RenovateReport } from './mapReport';

/**
 * dependency-companion-pins-PRD F2: a `follow:` pin becomes a lookup record of its leader (manager `follow`), found by
 * the action in the checkout, with the leader's candidates mapped to the follower's version (S7). The input is the
 * S1 fixture (test/fixtures/renovate/custom-managers): SeoTester's Chromium image at Playwright 1.63.0.
 */
const IMAGE = 'mcr.microsoft.com/playwright';
const leaders = () => mapReport(readJsonFixture<RenovateReport>('renovate/custom-managers/report.json')).filter((r) => r.manager === 'dockerfile' && r.packageFile === 'chromium/Dockerfile');
const compose: FollowRule = { dependency: IMAGE, path: 'docker-compose.yml', match: 'seotester-chromium:(?<version>[0-9.]+)' };

function sample(): string {
  const root = tempDir();
  copyTree(fixture('renovate/custom-managers/input'), root);
  return root;
}

async function follow(rules: FollowRule[], records: LookupRecord[] = leaders(), root = sample()) {
  const problems: LookupDocument['problems'] = [];
  const followers = await followerRecords(directoryFiles(root), rules, records, problems);
  return { followers, problems };
}

describe('followerVersion (S7)', () => {
  it.each([
    [undefined, 'v1.64.1-noble', '1.64.1'],
    [undefined, '1.65.0-beta.2', '1.65.0-beta.2'],
    [undefined, '11.0.0-rc.2-noble', '11.0.0-rc.2'],
    [undefined, '22.11.0-alpine3.20', '22.11.0'],
    [undefined, 'noble', null],
    ['^v(?<version>[0-9.]+)-noble$', 'v1.64.1-noble', '1.64.1'],
    ['^v(?<version>[0-9.]+)-noble$', 'v1.64.1-jammy', null],
  ])('version %s maps %s to %s, as the server does (DependencyFollowRule.FollowerVersion)', (version, leader, expected) => {
    expect(followerVersion({ ...compose, version }, leader)).toBe(expected);
  });
});

describe('followerRecords (F2)', () => {
  it('turns the own image tag into a record of its leader, with the leader candidates as follower versions', async () => {
    const { followers, problems } = await follow([compose]);

    expect(problems).toEqual([]);
    expect(followers).toHaveLength(1);
    expect(followers[0]).toMatchObject({
      manager: 'follow', depName: IMAGE, datasource: 'docker', ecosystem: 'docker', packageFile: 'docker-compose.yml',
      currentValue: '1.63.0', replaceString: 'seotester-chromium:1.63.0', currentDigest: null, hold: false,
    });
    expect(followers[0].candidates.map((c) => [c.value, c.updateType])).toEqual([['1.64.1', 'minor'], ['1.65.1', 'minor']]);
  });

  it('applies a version: pattern to the leader values', async () => {
    const { followers } = await follow([{ ...compose, version: '^v(?<version>[0-9]+\\.[0-9]+)\\.[0-9]+-noble$' }]);
    expect(followers[0].candidates.map((c) => c.value)).toEqual(['1.64', '1.65']);
  });

  it('catches up a follower that lags its leader, the half-move of #669', async () => {
    const moved = leaders().map((r) => ({ ...r, currentValue: 'v1.64.1-noble', candidates: r.candidates.filter((c) => c.value === 'v1.65.1-noble') }));
    const { followers } = await follow([compose], moved);
    expect(followers[0].candidates.map((c) => [c.value, c.updateType])).toEqual([['1.64.1', 'minor'], ['1.65.1', 'minor']]);
  });

  it('finds every occurrence, each its own record, so the group plans all of them', async () => {
    const root = sample();
    fs.appendFileSync(path.join(root, 'docker-compose.yml'), '  again:\n    image: ghcr.io/mintplayer/seotester-chromium:1.63.0\n');
    const { followers } = await follow([compose], leaders(), root);
    expect(followers.map((f) => f.replaceString)).toEqual(['seotester-chromium:1.63.0', 'seotester-chromium:1.63.0']);
  });

  it('matches a path glob', async () => {
    const { followers } = await follow([{ dependency: IMAGE, path: '**/Dockerfile', match: 'playwright-core@(?<version>[0-9.]+)' }]);
    expect(followers.map((f) => f.packageFile)).toEqual(['chromium/Dockerfile']);
  });

  it.each([
    ['a dependency the lookup did not report', { ...compose, dependency: 'Microsoft.Playwright' }, /Microsoft\.Playwright.*not.*lookup/],
    ['a pattern that matches nothing', { ...compose, match: 'nothing-here:(?<version>[0-9.]+)' }, /matched nothing/],
    ['a path that matches no file', { ...compose, path: 'apps/**/compose.yaml' }, /matched nothing/],
    ['a pattern JavaScript cannot compile', { ...compose, match: 'seotester-chromium:(?<version>(?>[0-9.]+))' }, /not a JavaScript regular expression/],
    ['a path under .github', { ...compose, path: '.github/**' }, /\.github/],
  ])('reports %s as a problem of the rule, and moves nothing', async (_name, rule, message) => {
    const { followers, problems } = await follow([rule]);
    expect(followers).toEqual([]);
    expect(problems).toHaveLength(1);
    expect(problems[0].source).toBe('follow[0]');
    expect(problems[0].message).toMatch(message);
  });
});
