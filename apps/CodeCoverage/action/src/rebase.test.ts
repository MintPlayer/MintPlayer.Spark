import { describe, expect, it } from 'vitest';
import { isRebasableReport, rebaseReportContent } from './rebase';
import { rebasePath, toPosixPath, workspacePrefix } from './paths';

// A Windows workspace root, written literally rather than built from `path`, so
// these assertions hold identically on a Linux CI runner. The whole point is
// behaviour on paths this host does not produce.
const WINDOWS_ROOT = 'D:\\a\\MintPlayer.DotnetDesktop.Tools\\MintPlayer.DotnetDesktop.Tools';
const POSIX_ROOT = '/home/runner/work/repo/repo';

describe('toPosixPath', () => {
  it('converts separators and leaves an already-posix path alone', () => {
    expect(toPosixPath('D:\\a\\repo\\src\\App.cs')).toBe('D:/a/repo/src/App.cs');
    expect(toPosixPath('src/App.cs')).toBe('src/App.cs');
  });
});

describe('workspacePrefix', () => {
  it('normalises to exactly one trailing slash', () => {
    expect(workspacePrefix('D:\\a\\repo')).toBe('D:/a/repo/');
    expect(workspacePrefix('/home/runner/work/repo/')).toBe('/home/runner/work/repo/');
    expect(workspacePrefix('/home/runner/work/repo///')).toBe('/home/runner/work/repo/');
  });

  it('does not let one repository prefix-match another', () => {
    // Without the trailing slash, `repo` would swallow the leading `sitory/` of
    // `repository`, producing a path that looks plausible and resolves to nothing.
    const prefix = workspacePrefix('/work/repo');
    expect(rebasePath('/work/repository/src/App.cs', prefix)).toBe('/work/repository/src/App.cs');
  });
});

describe('rebasePath', () => {
  it('strips the workspace root case-insensitively', () => {
    // A Windows collector and git can disagree on the drive letter's case.
    const prefix = workspacePrefix(WINDOWS_ROOT);
    expect(rebasePath('d:\\A\\MintPlayer.DotnetDesktop.Tools\\MintPlayer.DotnetDesktop.Tools\\src\\App.cs', prefix))
      .toBe('src/App.cs');
  });

  it('unifies but does not strip a path that is not under the root', () => {
    expect(rebasePath('C:\\elsewhere\\App.cs', workspacePrefix(WINDOWS_ROOT))).toBe('C:/elsewhere/App.cs');
  });
});

describe('rebaseReportContent', () => {
  it('rewrites absolute windows cobertura filenames', () => {
    const xml =
      `<class filename="${WINDOWS_ROOT}\\ThreeDee\\Geometry\\Mesh.cs" />` +
      `<class filename="${WINDOWS_ROOT}\\ThreeDee\\Geometry\\Face.cs" />`;

    const { content, rewritten } = rebaseReportContent(xml, WINDOWS_ROOT);

    expect(rewritten).toBe(2);
    expect(content).toContain('filename="ThreeDee/Geometry/Mesh.cs"');
    expect(content).toContain('filename="ThreeDee/Geometry/Face.cs"');
    expect(content).not.toContain('D:\\');
  });

  it('rewrites absolute lcov SF: lines', () => {
    const lcov = `TN:\nSF:${POSIX_ROOT}/libs/seo/src/index.ts\nDA:1,1\nend_of_record\n`;

    const { content, rewritten } = rebaseReportContent(lcov, POSIX_ROOT);

    expect(rewritten).toBe(1);
    expect(content).toContain('SF:libs/seo/src/index.ts');
  });

  it('rewrites jacoco sourcefile names that carry a full path', () => {
    const xml = `<sourcefile name="${POSIX_ROOT}/src/main/java/com/acme/App.java">`;
    const { content, rewritten } = rebaseReportContent(xml, POSIX_ROOT);

    expect(rewritten).toBe(1);
    expect(content).toContain('sourcefile name="src/main/java/com/acme/App.java"');
  });

  it('leaves an already repository-relative report untouched', () => {
    // The normal case on Linux. Rewriting nothing is success, not a problem --
    // which is why this does not fail the step the way the consumer-side script did.
    const lcov = 'TN:\nSF:src/app.ts\nDA:1,1\nend_of_record\n';
    const { content, rewritten } = rebaseReportContent(lcov, POSIX_ROOT);

    expect(rewritten).toBe(0);
    expect(content).toBe(lcov);
  });

  it('is idempotent', () => {
    const xml = `<class filename="${WINDOWS_ROOT}\\src\\App.cs" />`;

    const first = rebaseReportContent(xml, WINDOWS_ROOT);
    const second = rebaseReportContent(first.content, WINDOWS_ROOT);

    expect(first.rewritten).toBe(1);
    expect(second.rewritten).toBe(0);
    expect(second.content).toBe(first.content);
  });

  it('does not touch paths outside the workspace beyond unifying separators', () => {
    const xml = '<class filename="C:\\Program Files\\dotnet\\shared\\System.Private.CoreLib.dll" />';
    const { content } = rebaseReportContent(xml, WINDOWS_ROOT);

    expect(content).toContain('filename="C:/Program Files/dotnet/shared/System.Private.CoreLib.dll"');
  });
});

describe('isRebasableReport', () => {
  it('accepts the report formats whose path tokens we understand', () => {
    expect(isRebasableReport('/w/coverage/report.cobertura.xml')).toBe(true);
    expect(isRebasableReport('/w/coverage/lcov.info')).toBe(true);
  });

  it('rejects anything else, so an unknown payload is uploaded byte-for-byte', () => {
    expect(isRebasableReport('/w/coverage/report.json')).toBe(false);
    expect(isRebasableReport('/w/coverage/coverage.bin')).toBe(false);
  });
});
