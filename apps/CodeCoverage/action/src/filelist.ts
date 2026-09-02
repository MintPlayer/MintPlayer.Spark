/**
 * Reduces `git ls-files -s` output (`<mode> <oid> <stage>\t<path>`) to the
 * server's file-list v2 wire format: one `<oid> <path>` per line. Entries in a
 * merge stage other than 0 are dropped — a conflicted index has no single
 * blob for that path — and lines that don't parse are skipped rather than
 * passed through as bare paths, so the payload is never a mix of formats.
 */
export function formatFileList(lsFilesStageOutput: string): string {
  const lines: string[] = [];
  for (const raw of lsFilesStageOutput.split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (!line) continue;
    const tab = line.indexOf('\t');
    if (tab < 0) continue;
    const meta = line.slice(0, tab).split(/\s+/);
    const filePath = line.slice(tab + 1);
    if (meta.length !== 3 || !filePath) continue;
    const [, oid, stage] = meta;
    if (stage !== '0' || !/^[0-9a-f]{40,64}$/i.test(oid)) continue;
    lines.push(`${oid.toLowerCase()} ${filePath}`);
  }
  return lines.join('\n');
}
