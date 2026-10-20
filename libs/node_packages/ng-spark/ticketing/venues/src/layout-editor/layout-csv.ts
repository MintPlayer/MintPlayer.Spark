import type { LayoutBlock, LayoutPlan, LayoutRow, LayoutSeat, VenueLayoutProblem } from '../generated/contracts';
import { TAG_ACCESSIBLE, TAG_COMPANION, TAG_RESTRICTED_VIEW } from '../seat-map/seat-map-model';
import { slugKey } from './seat-generator';

// CSV seat import and export in the browser (D32), the same format as the server's LayoutCsv:
//   block,row,seat,x,y,category,tags,companion_of,key
// Files are read with the File API and written with a Blob (K11: standard APIs, no round trip).

export const CSV_HEADER = ['block', 'row', 'seat', 'x', 'y', 'category', 'tags', 'companion_of', 'key'];

export const TAG_NAMES: readonly (readonly [number, string])[] = [
  [TAG_ACCESSIBLE, 'accessible'],
  [TAG_COMPANION, 'companion'],
  [TAG_RESTRICTED_VIEW, 'restricted-view'],
];

export function tagsText(tags: number): string {
  return TAG_NAMES.filter(([flag]) => tags & flag).map(([, name]) => name).join('|');
}

/** RFC 4180 text of every numbered seat, with spreadsheet formula injection guarded (OWASP). */
export function exportCsv(plan: LayoutPlan): string {
  const lines = [CSV_HEADER.join(',')];
  for (const block of plan.blocks) {
    if (block.kind !== 'seatedNumbered') continue;
    for (const row of block.rows) {
      for (const seat of row.seats) {
        lines.push([
          text(block.key), text(row.label), text(seat.label), String(seat.x), String(seat.y),
          text(seat.category ?? ''), text(tagsText(seat.tags)), text(seat.companionOf ?? ''), text(seat.key),
        ].join(','));
      }
    }
  }
  return lines.join('\r\n') + '\r\n';
}

/** The plan with the file's seats merged in (the blocks it names get its rows), or the problems. Pure. */
export function importCsv(plan: LayoutPlan, csv: string): { plan: LayoutPlan | null; problems: VenueLayoutProblem[] } {
  const records = parseCsv(csv);
  const header = (records[0] ?? []).map((h) => h.trim().toLowerCase());
  if (CSV_HEADER.slice(0, 5).some((h, i) => header[i] !== h)) return { plan: null, problems: [{ path: 'line 1', code: 'ticketingVenues.csv.header' }] };
  const column = (record: string[], name: string) => {
    const i = header.indexOf(name);
    return i >= 0 && i < record.length ? unguard(record[i].trim()) : '';
  };

  const problems: VenueLayoutProblem[] = [];
  const byBlock = new Map<string, { row: string; seat: LayoutSeat }[]>();
  for (let line = 1; line < records.length; line++) {
    const record = records[line];
    if (record.length === 1 && record[0] === '') continue;
    const path = `line ${line + 1}`;
    const blockKey = column(record, 'block'), rowLabel = column(record, 'row'), seatLabel = column(record, 'seat');
    if (!/^[A-Za-z0-9_.-]{1,64}$/.test(blockKey) || !rowLabel || !seatLabel) { problems.push({ path, code: 'ticketingVenues.csv.missing' }); continue; }
    const x = Number(column(record, 'x')), y = Number(column(record, 'y'));
    if (column(record, 'x') === '' || column(record, 'y') === '' || !Number.isFinite(x) || !Number.isFinite(y)) { problems.push({ path, code: 'ticketingVenues.csv.position' }); continue; }
    let tags = 0;
    for (const name of column(record, 'tags').split('|').map((t) => t.trim()).filter(Boolean)) {
      const flag = TAG_NAMES.find(([, n]) => n === name.toLowerCase())?.[0];
      if (!flag) problems.push({ path, code: 'ticketingVenues.csv.tag' });
      else tags |= flag;
    }
    const seat: LayoutSeat = {
      key: column(record, 'key') || slugKey(`${blockKey}-${rowLabel}-${seatLabel}`),
      label: seatLabel, x, y, tags,
      category: column(record, 'category') || null,
      companionOf: column(record, 'companion_of') || null,
    };
    if (!byBlock.has(blockKey)) byBlock.set(blockKey, []);
    byBlock.get(blockKey)!.push({ row: rowLabel, seat });
  }
  if (problems.length > 0) return { plan: null, problems };

  const blocks = [...plan.blocks];
  for (const [blockKey, seats] of byBlock) {
    const index = blocks.findIndex((b) => b.key === blockKey);
    const existing = index >= 0 ? blocks[index] : null;
    if (existing && existing.kind !== 'seatedNumbered') { problems.push({ path: `block:${blockKey}`, code: 'ticketingVenues.csv.notNumbered' }); continue; }
    const previous = new Map((existing?.rows ?? []).map((r) => [r.label, r.key]));
    const rows: LayoutRow[] = [];
    const rowIndex = new Map<string, LayoutRow>();
    for (const { row, seat } of seats) {
      let target = rowIndex.get(row);
      if (!target) {
        target = { key: previous.get(row) ?? slugKey(row), label: row, seats: [] };
        rowIndex.set(row, target);
        rows.push(target);
      }
      target.seats.push(seat);
    }
    const block: LayoutBlock = existing
      ? { ...existing, rows }
      : { key: blockKey, name: blockKey, kind: 'seatedNumbered', polygon: boundingBox(seats.map((s) => s.seat), 10), capacity: 0, category: null, rows };
    if (index >= 0) blocks[index] = block; else blocks.push(block);
  }
  return problems.length > 0 ? { plan: null, problems } : { plan: { ...plan, blocks }, problems: [] };
}

/** RFC 4180 records (quoted fields may hold commas, doubled quotes and line breaks). */
export function parseCsv(csv: string): string[][] {
  const records: string[][] = [];
  let record: string[] = [];
  let field = '';
  let quoted = false;
  for (let i = csv.charCodeAt(0) === 0xfeff ? 1 : 0; i < csv.length; i++) {
    const c = csv[i];
    if (quoted) {
      if (c === '"' && csv[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { record.push(field); field = ''; }
    else if (c === '\r' || c === '\n') {
      if (c === '\r' && csv[i + 1] === '\n') i++;
      record.push(field); field = '';
      records.push(record); record = [];
    } else field += c;
  }
  if (field !== '' || record.length > 0) { record.push(field); records.push(record); }
  return records;
}

/** A Blob of the CSV (with a BOM, so spreadsheets read UTF-8) for a download link. */
export function csvBlob(plan: LayoutPlan): Blob {
  return new Blob(['﻿' + exportCsv(plan)], { type: 'text/csv;charset=utf-8' });
}

function text(value: string): string {
  if (/^[=+\-@\t\r]/.test(value)) value = `'${value}`;
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

function unguard(value: string): string {
  return /^'[=+\-@\t\r]/.test(value) ? value.slice(1) : value;
}

function boundingBox(seats: LayoutSeat[], margin: number): number[] {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const s of seats) { x0 = Math.min(x0, s.x); y0 = Math.min(y0, s.y); x1 = Math.max(x1, s.x); y1 = Math.max(y1, s.y); }
  x0 -= margin; y0 -= margin; x1 += margin; y1 += margin;
  return [x0, y0, x1, y0, x1, y1, x0, y1];
}
