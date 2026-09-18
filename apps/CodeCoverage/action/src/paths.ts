/**
 * Path helpers shared by the action.
 *
 * There are two path *roles* here and conflating them is a bug waiting to happen:
 *
 * - **Native** — what the runner's filesystem uses. Anything handed to `glob`,
 *   `fs` or `git -C` must stay native, because those re-normalise separators
 *   themselves and a converted path buys nothing.
 * - **Wire** — what leaves this process: the `rootDir` form field and the paths
 *   embedded in the reports we upload. Always forward-slash, on every runner OS.
 *
 * `ctx.rootDir` stays native. It is converted at the points where it crosses the
 * wire, and nowhere else.
 */

/** The canonical wire form: forward slashes. */
export const toPosixPath = (value: string): string => value.replace(/\\/g, '/');

/**
 * The workspace root as a prefix to strip: posix, with exactly one trailing
 * slash so `D:/a/repo/repo` never matches `D:/a/repo/repository`.
 */
export const workspacePrefix = (rootDir: string): string => toPosixPath(rootDir).replace(/\/+$/, '') + '/';

/**
 * Turns one report-internal path into the repository-relative, forward-slash
 * form the server resolves against `git ls-files`.
 *
 * Case-insensitive prefix comparison because a Windows collector and `git` can
 * disagree on the drive letter's case (`D:\` vs `d:\`) for the same directory.
 * A path that does not carry the prefix is returned unified but otherwise
 * untouched — a report that is already repo-relative must survive unchanged.
 */
export function rebasePath(rawPath: string, prefix: string): string {
  const unified = toPosixPath(rawPath);
  return unified.toLowerCase().startsWith(prefix.toLowerCase()) ? unified.slice(prefix.length) : unified;
}
