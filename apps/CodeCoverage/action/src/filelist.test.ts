import { describe, expect, it } from 'vitest';
import { formatFileList } from './filelist';

const oidA = 'a'.repeat(40);
const oidB = 'B'.repeat(40);

describe('formatFileList', () => {
  it('reduces ls-files -s lines to oid and path', () => {
    const out = formatFileList(`100644 ${oidA} 0\tsrc/a.cs\n100755 ${oidB} 0\tbin/run.sh\n`);
    expect(out).toBe(`${oidA} src/a.cs\n${'b'.repeat(40)} bin/run.sh`);
  });

  it('keeps spaces inside paths and tolerates CRLF', () => {
    const out = formatFileList(`100644 ${oidA} 0\tdocs/read me.md\r\n`);
    expect(out).toBe(`${oidA} docs/read me.md`);
  });

  it('drops conflicted stages and unparsable lines', () => {
    const out = formatFileList(`100644 ${oidA} 1\tsrc/conflict.cs\n100644 ${oidA} 2\tsrc/conflict.cs\nnot a real line\n100644 ${oidB} 0\tsrc/ok.cs\n`);
    expect(out).toBe(`${'b'.repeat(40)} src/ok.cs`);
  });

  it('is empty for empty input', () => {
    expect(formatFileList('')).toBe('');
  });
});
