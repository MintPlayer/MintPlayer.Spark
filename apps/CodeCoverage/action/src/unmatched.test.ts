import { beforeEach, describe, expect, it, vi } from 'vitest';
import * as core from '@actions/core';
import { reportUnmatched } from './main';
import { UploadStatus } from './status';

/**
 * The verdict that ends the silence in issue #415: a build whose files all
 * failed to resolve is accepted, finalized, green and empty, and nothing said
 * so. These assert the three judgements that behaviour turns on — total
 * failure, partial failure, and the cases where we deliberately say nothing.
 */
function status(unmatched: UploadStatus['unmatched']): UploadStatus {
  return { buildId: 'b', state: 'Complete', status: 'Complete', unmatched } as UploadStatus;
}

describe('reportUnmatched', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('throws when nothing resolved, so fail-ci-if-error decides what it means', () => {
    expect(() => reportUnmatched(status({ files: 7, totalFiles: 7, sample: ['D:/a/r/r/src/App.cs'] }), 7))
      .toThrow(/None of the uploaded coverage resolved/);
  });

  it('names a sample in the failure, because the shape of the path is the diagnosis', () => {
    expect(() => reportUnmatched(status({ files: 1, totalFiles: 1, sample: ['D:/a/r/r/src/App.cs'] }), 1))
      .toThrow(/D:\/a\/r\/r\/src\/App\.cs/);
  });

  it('warns rather than throws when only some files failed to resolve', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    reportUnmatched(status({ files: 3, totalFiles: 10, sample: ['src/a.ts'] }), 4);

    expect(warning).toHaveBeenCalledOnce();
    expect(warning.mock.calls[0][0]).toMatch(/3 of 10 file\(s\)/);
  });

  it('says nothing when every file matched', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    reportUnmatched(status({ files: 0, totalFiles: 10, sample: [] }), 4);

    expect(warning).not.toHaveBeenCalled();
  });

  // The nx-affected carry-forward path: every project was cached, so the upload
  // carries only a file list. It has no paths to resolve and must not start
  // failing now that we have an opinion about unmatched files.
  it('exempts a file-list-only upload', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    expect(() => reportUnmatched(status({ files: 0, totalFiles: 0, sample: [] }), 0)).not.toThrow();
    expect(warning).not.toHaveBeenCalled();
  });

  // Absent means "not known" -- an older server, or a build that has not
  // finalized. Neither is evidence that everything matched.
  it('draws no verdict when the server did not report unmatched counts', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    expect(() => reportUnmatched(status(undefined), 4)).not.toThrow();
    expect(() => reportUnmatched(status(null), 4)).not.toThrow();
    expect(warning).not.toHaveBeenCalled();
  });
});
