import { beforeEach, describe, expect, it, vi } from 'vitest';
import * as core from '@actions/core';
import { describeRejections, reportRejectedReports, setResultOutputs } from './main';
import { UploadStatus } from './status';

/**
 * Issue #417: a report the server could not use must reach the consumer by name and
 * with a reason. The upload behind #415 reported only "the build finalized with
 * errors", and getting from that to "a UTF-8 BOM" took a day and three wrong guesses.
 */
function status(overrides: Partial<UploadStatus>): UploadStatus {
  return { buildId: 'b', state: 'Complete', status: 'Complete', ...overrides } as UploadStatus;
}

describe('reportRejectedReports', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('names the file and the reason, one warning per rejected report', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    reportRejectedReports(status({
      ingest: {
        reportsAccepted: 1,
        reportsRejected: 2,
        rejected: [
          { fileName: 'coverage.cobertura.xml', reason: 'malformed', detail: 'Data at the root level is invalid.' },
          { fileName: 'lcov.info', reason: 'empty', detail: 'The file is empty (0 bytes).' },
        ],
      },
    }));

    expect(warning).toHaveBeenCalledTimes(2);
    expect(warning.mock.calls[0][0]).toMatch(/coverage\.cobertura\.xml/);
    expect(warning.mock.calls[0][0]).toMatch(/malformed/);
    // The parser's own message is the diagnosis -- this exact string is what #415
    // needed a day to surface.
    expect(warning.mock.calls[0][0]).toMatch(/Data at the root level is invalid/);
    expect(warning.mock.calls[1][0]).toMatch(/lcov\.info/);
  });

  it('says nothing when every report was accepted', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    reportRejectedReports(status({ ingest: { reportsAccepted: 3, reportsRejected: 0, rejected: [] } }));

    expect(warning).not.toHaveBeenCalled();
  });

  /**
   * Absent `ingest` is an older server, which means "not known" -- never "nothing was
   * rejected". Drawing a verdict from a field the server never sent is precisely the
   * class of mistake this whole issue is about.
   */
  it('draws no verdict against a server that does not report ingest outcomes', () => {
    const warning = vi.spyOn(core, 'warning').mockImplementation(() => {});

    reportRejectedReports(status({}));
    reportRejectedReports(status({ ingest: null }));

    expect(warning).not.toHaveBeenCalled();
  });
});

describe('describeRejections', () => {
  it('joins every rejection so the thrown error carries all of them', () => {
    const text = describeRejections(status({
      ingest: {
        reportsAccepted: 0,
        reportsRejected: 2,
        rejected: [
          { fileName: 'a.xml', reason: 'truncated', detail: 'Unexpected end of file.' },
          { fileName: 'b.info', reason: 'unrecognizedFormat', detail: null },
        ],
      },
    }));

    expect(text).toMatch(/a\.xml: truncated/);
    expect(text).toMatch(/b\.info: unrecognizedFormat/);
  });

  it('is empty against an older server, so the caller falls back to session errors', () => {
    expect(describeRejections(status({}))).toBe('');
  });
});

describe('files-count', () => {
  beforeEach(() => vi.restoreAllMocks());

  function outputsFrom(s: UploadStatus): Map<string, string> {
    const outputs = new Map<string, string>();
    vi.spyOn(core, 'setOutput').mockImplementation((name, value) => {
      outputs.set(name, String(value));
    });
    setResultOutputs(s);
    return outputs;
  }

  /**
   * THE #415 CONSUMER-GUARD REGRESSION.
   *
   * The build finalized with zero files measured, `coverage` came back null, and this
   * output was the EMPTY STRING -- so a consumer guard testing `== "0"` never fired and
   * the run shipped green with a blank report page. Empty and zero must not be
   * different things.
   */
  it('is 0, not empty, on a terminal build that measured nothing', () => {
    const outputs = outputsFrom(status({ state: 'CompleteWithErrors', coverage: null } as Partial<UploadStatus>));

    expect(outputs.get('files-count')).toBe('0');
  });

  it('is 0 on a clean terminal build that happens to have no coverage', () => {
    expect(outputsFrom(status({ state: 'Complete', coverage: null } as Partial<UploadStatus>)).get('files-count')).toBe('0');
  });

  /**
   * Still in flight means the number genuinely is not known yet, and asserting 0 there
   * would be a different lie from the one being fixed.
   */
  it('stays empty while the build is still in flight', () => {
    const outputs = outputsFrom(status({ state: 'InFlight', coverage: null } as Partial<UploadStatus>));

    expect(outputs.get('files-count')).toBe('');
  });

  it('reports the real count when there is one', () => {
    const outputs = outputsFrom(status({
      state: 'Complete',
      coverage: { linesCovered: 1, linesCoverable: 2, branchesCovered: 0, branchesTotal: 0, filesCount: 79 },
    } as Partial<UploadStatus>));

    expect(outputs.get('files-count')).toBe('79');
  });
});
