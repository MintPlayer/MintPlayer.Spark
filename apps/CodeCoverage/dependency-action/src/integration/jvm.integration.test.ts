import { pathToFileURL } from 'node:url';
import { item, tempDir } from '../testing/helpers';
import { mavenRepo } from './registries';
import { applyOnRepo, enabled, must } from './setup';

/**
 * jvm slice: Maven and Gradle resolve the DEPENDENCY from a file:// repository. Their own plugins still
 * come from the tool's default repository (a fresh runner has no ~/.m2), so this slice is the one that
 * needs network for tooling; the artifacts under test never leave the local repo.
 */

describe.skipIf(!enabled('jvm', 'mvn'))('Maven against a file:// repository', () => {
  const repo = tempDir();
  beforeAll(() => mavenRepo(repo, [
    { group: 'com.acme', artifact: 'liba', version: '1.0.0' }, { group: 'com.acme', artifact: 'liba', version: '1.1.0' },
  ]));
  // Not `local`: Maven 3.10 reserves that id for the local repository and refuses to build the POM.
  const pom = (prop: string, inline: string) => `<?xml version="1.0" encoding="UTF-8"?>
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>it</groupId><artifactId>app</artifactId><version>1.0.0</version>
  <properties>
    <liba.version>${prop}</liba.version>   <!-- shared -->
  </properties>
  <repositories><repository><id>it-file-repo</id><url>${pathToFileURL(repo).href}</url></repository></repositories>
  <dependencies>
    <dependency><groupId>com.acme</groupId><artifactId>liba</artifactId><version>\${liba.version}</version></dependency>
  </dependencies>
</project>
${inline}`;

  it('edits the property, resolves; a missing version is a maven blocker', async () => {
    const ok = await applyOnRepo({ 'pom.xml': pom('1.0.0', '') }, [
      { group: 'liba', items: [item({ depName: 'com.acme:liba', datasource: 'maven', manager: 'maven', packageFile: 'pom.xml', currentValue: '1.0.0', newValue: '1.1.0', sharedVariableName: 'liba.version' })] },
    ]);
    expect(ok.result.groups[0].status).toBe('applied');
    expect(ok.read('pom.xml')).toBe(pom('1.1.0', ''));
    const missing = await applyOnRepo({ 'pom.xml': pom('1.0.0', '') }, [
      { group: 'liba', items: [item({ depName: 'com.acme:liba', datasource: 'maven', manager: 'maven', packageFile: 'pom.xml', currentValue: '1.0.0', newValue: '9.9.9', sharedVariableName: 'liba.version' })] },
    ]);
    expect(missing.result.groups[0]).toMatchObject({ status: 'unresolvable', blockers: [expect.objectContaining({ depName: 'com.acme:liba', version: '9.9.9' })] });
  });
});

describe.skipIf(!enabled('jvm', 'gradle'))('Gradle against a file:// repository', () => {
  const repo = tempDir();
  beforeAll(() => mavenRepo(repo, [
    { group: 'com.acme', artifact: 'liba', version: '1.0.0' }, { group: 'com.acme', artifact: 'liba', version: '1.1.0' },
  ]));
  const files = () => ({
    'settings.gradle': 'rootProject.name = "it"\n',
    'build.gradle': `plugins { id 'java-library' }\nrepositories { maven { url = uri("${pathToFileURL(repo).href}") } }\ndependencyLocking { lockAllConfigurations() }\ndependencies { implementation(libs.liba) }\n`,
    'gradle/libs.versions.toml': '[libraries]\nliba = { module = "com.acme:liba", version = "1.0.0" }\n',
  });

  it('catalog edit + --write-locks; a missing version prints FAILED with exit 0 and is still caught (S13)', async () => {
    const prepare = (dir: string) => must('gradle', ['--no-daemon', '-q', 'dependencies', '--write-locks'], dir).then(() => undefined);
    const ok = await applyOnRepo(files(), [
      { group: 'liba', items: [item({ depName: 'com.acme:liba', datasource: 'maven', manager: 'gradle', packageFile: 'gradle/libs.versions.toml', currentValue: '1.0.0', newValue: '1.1.0' })] },
    ], { prepare });
    expect(ok.result.groups[0].status).toBe('applied');
    expect(ok.read('gradle.lockfile')).toContain('com.acme:liba:1.1.0=');
    const missing = await applyOnRepo(files(), [
      { group: 'liba', items: [item({ depName: 'com.acme:liba', datasource: 'maven', manager: 'gradle', packageFile: 'gradle/libs.versions.toml', currentValue: '1.0.0', newValue: '9.9.9' })] },
    ], { prepare });
    expect(missing.result.groups[0]).toMatchObject({ status: 'unresolvable', blockers: [expect.objectContaining({ depName: 'com.acme:liba', code: 'gradle-FAILED' })] });
  });
});
