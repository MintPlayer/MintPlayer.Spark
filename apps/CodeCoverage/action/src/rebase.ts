import { rebasePath, workspacePrefix } from './paths';

/**
 * Rewrites the paths *inside* a coverage report to repository-relative,
 * forward-slash form before it is uploaded.
 *
 * <h3>Why this lives in the action</h3>
 *
 * The server normalises paths too, and does it well. This is deliberate
 * duplication, and the reason is a requirement rather than a mechanism: **no
 * consumer repository may carry path-rebasing code.** Two of them did — a
 * PowerShell script in one, a Node script in another — because a collector
 * running on a non-Linux runner writes absolute native paths and the upload
 * then produced an empty report. Only the action can retire those scripts: it
 * is the layer that knows `GITHUB_WORKSPACE` for certain, and it is what a
 * consumer's pinned tag actually delivers. Fixing only the server leaves every
 * consumer carrying a script if the cause turns out to be something else.
 *
 * <h3>What it deliberately does not do</h3>
 *
 * It does not parse coverage. It recognises the one path-bearing token per
 * format and rewrites it, which is why adding a format is a line rather than a
 * parser. It also does not fail when nothing was rewritten: a report that is
 * already repository-relative rewrites nothing, and that is the normal case on
 * Linux. The loud failure for "this upload will produce an empty report" is the
 * server's unmatched-file verdict, which is accurate where a rewrite count is
 * only a proxy.
 */

/**
 * One path-bearing token per supported format.
 *
 * - `filename="…"` — Cobertura `<class>`, and Clover, which shares the attribute.
 * - `SF:…` — lcov, to end of line.
 * - `sourcefile name="…"` — JaCoCo. Its `name` is normally a bare file name with
 *   the directory in the enclosing `<package>`, so this is usually a no-op; it
 *   is here for the generators that write a full path anyway.
 */
const TOKENS: { pattern: RegExp; rebuild: (prefix: string, rebased: string) => string }[] = [
  {
    pattern: /filename="([^"]*)"/g,
    rebuild: (_prefix, rebased) => `filename="${rebased}"`,
  },
  {
    pattern: /sourcefile name="([^"]*)"/g,
    rebuild: (_prefix, rebased) => `sourcefile name="${rebased}"`,
  },
  {
    pattern: /^SF:(.*)$/gm,
    rebuild: (_prefix, rebased) => `SF:${rebased}`,
  },
];

export interface RebaseResult {
  content: string;
  /** How many tokens changed. Zero is normal for an already-relative report. */
  rewritten: number;
}

/**
 * Rebases every recognised path token in `content` against `rootDir`.
 * Idempotent: the output of one pass rewrites nothing on a second.
 */
export function rebaseReportContent(content: string, rootDir: string): RebaseResult {
  const prefix = workspacePrefix(rootDir);
  let rewritten = 0;

  let result = content;
  for (const { pattern, rebuild } of TOKENS) {
    result = result.replace(pattern, (whole, captured: string) => {
      const rebased = rebasePath(captured, prefix);
      if (rebased === captured) return whole;
      rewritten++;
      return rebuild(prefix, rebased);
    });
  }

  return { content: result, rewritten };
}

/**
 * True when the report is one whose paths we know how to rewrite. Anything else
 * is uploaded byte-for-byte — rewriting a format we do not understand is how a
 * well-meaning normaliser corrupts a payload.
 */
export function isRebasableReport(filePath: string): boolean {
  return /\.(xml|info|lcov|dat)$/i.test(filePath);
}
