/** One line of a line diff: unchanged, only in the new text, or only in the old one. */
export interface SparkDiffLine {
  kind: 'same' | 'added' | 'removed';
  text: string;
}

/** Above this many cells (old lines × new lines) the LCS table is not built; see {@link lineDiff}. */
const MAX_CELLS = 4_000_000;

/** Splits a text into lines (`\r\n`, `\r` and `\n` alike); an empty or absent text has none. */
export function splitLines(text: string | null | undefined): string[] {
  if (text === null || text === undefined || text === '') return [];
  return String(text).split(/\r\n|\r|\n/);
}

/**
 * A line diff of `newText` against `oldText`: the longest common subsequence of their lines kept as
 * `same`, everything else `removed` (old) or `added` (new), in reading order — at a change, the old
 * lines come before the new ones. Pure, no dependency.
 *
 * Beyond {@link MAX_CELLS} cells (thousands of lines on both sides) the common prefix and suffix are
 * still matched, and the middle is reported as removed-then-added rather than building a huge table.
 */
export function lineDiff(oldText: string | null | undefined, newText: string | null | undefined): SparkDiffLine[] {
  const a = splitLines(oldText);
  const b = splitLines(newText);

  // Common prefix and suffix first: most edits touch a few lines, and this keeps the table small.
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }

  const head: SparkDiffLine[] = a.slice(0, start).map(text => ({ kind: 'same', text }));
  const tail: SparkDiffLine[] = a.slice(endA).map(text => ({ kind: 'same', text }));
  const midA = a.slice(start, endA);
  const midB = b.slice(start, endB);

  return [...head, ...diffMiddle(midA, midB), ...tail];
}

function diffMiddle(a: string[], b: string[]): SparkDiffLine[] {
  const n = a.length;
  const m = b.length;
  if (n === 0) return b.map(text => ({ kind: 'added', text }));
  if (m === 0) return a.map(text => ({ kind: 'removed', text }));
  if (n * m > MAX_CELLS) {
    return [...a.map(text => ({ kind: 'removed' as const, text })), ...b.map(text => ({ kind: 'added' as const, text }))];
  }

  // lcs[i][j] = LCS length of a[i..] and b[j..], flattened row-major with a (m+1) stride.
  const stride = m + 1;
  const lcs = new Uint32Array((n + 1) * stride);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i * stride + j] = a[i] === b[j]
        ? lcs[(i + 1) * stride + j + 1] + 1
        : Math.max(lcs[(i + 1) * stride + j], lcs[i * stride + j + 1]);
    }
  }

  const result: SparkDiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      result.push({ kind: 'same', text: a[i] });
      i++; j++;
    } else if (lcs[(i + 1) * stride + j] >= lcs[i * stride + j + 1]) {
      result.push({ kind: 'removed', text: a[i++] });
    } else {
      result.push({ kind: 'added', text: b[j++] });
    }
  }
  while (i < n) result.push({ kind: 'removed', text: a[i++] });
  while (j < m) result.push({ kind: 'added', text: b[j++] });
  return result;
}

/** Whether a diff changes anything. */
export function diffHasChanges(lines: readonly SparkDiffLine[]): boolean {
  return lines.some(l => l.kind !== 'same');
}
