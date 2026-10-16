import * as fs from 'node:fs';
import * as path from 'node:path';
import { isValid } from '../contracts/validate';
import { runLookup } from '../lookup/lookup';
import { gitRepo, tempDir } from '../testing/helpers';
import { realExec } from '../util/exec';
import { enabled } from './setup';

/**
 * docker+renovate slice: the pinned Renovate image in lookup mode on a repository with planted hooks.
 * R17 guard 2 as a regression test (re-run on every digest bump): lookup runs NO repository code, so no
 * sentinel ever appears. Needs Docker and the public registries (Renovate asks them); skipped on Windows.
 */
const runnable = process.platform !== 'win32' && enabled('docker+renovate', 'docker');

describe.skipIf(!runnable)('Renovate lookup (pinned image)', () => {
  it('maps the report and runs none of the planted hooks', async () => {
    const { dir, sha } = gitRepo({
      'package.json': JSON.stringify({ name: 'it', version: '1.0.0', scripts: { preinstall: 'touch SENTINEL-npm', postinstall: 'touch SENTINEL-npm' }, dependencies: { 'left-pad': '1.0.0' } }, null, 2),
      '.npmrc': 'ignore-scripts=false\n',
      'build.gradle': "new File(rootDir, 'SENTINEL-gradle').text = 'ran'\nrepositories { mavenCentral() }\n",
      'init.gradle': "new File('SENTINEL-gradle-init').text = 'ran'\n",
      '.mvn/extensions.xml': '<extensions><extension><groupId>com.example</groupId><artifactId>sentinel</artifactId><version>1.0.0</version></extension></extensions>\n',
      'pom.xml': '<project><modelVersion>4.0.0</modelVersion><groupId>it</groupId><artifactId>it</artifactId><version>1</version></project>\n',
      Dockerfile: 'FROM node:20.11.0-alpine\nRUN touch /SENTINEL-docker\n',
    });
    const out = tempDir();
    const { document } = await runLookup({
      exec: realExec, repoDir: dir, outDir: out, workDir: tempDir(), expectedSha: sha,
      fetcher: async () => ({ 'releases-index': [] }), fetchActionYml: async () => null,
      githubTokenEnv: process.env.GITHUB_TOKEN ? 'GITHUB_TOKEN' : undefined,
    });
    expect(isValid('lookup', document).errors).toBe('');
    expect(document.records.some((r) => r.depName === 'left-pad')).toBe(true);
    const sentinels = fs.readdirSync(dir, { recursive: true }).map(String).filter((f) => path.basename(f).startsWith('SENTINEL'));
    expect(sentinels).toEqual([]);
  });
});
