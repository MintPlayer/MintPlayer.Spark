import { describe, expect, it } from 'vitest';
import { sparkDatatableLabels } from './spark-datatable-labels';

const translations: Record<string, string> = {
  'common.resizeColumn': 'Redimensionner la colonne {column}',
};
const lang = {
  t: (key: string) => translations[key] ?? key,
  resolve: (ts: Record<string, string> | undefined) => ts?.['fr'] ?? ts?.['en'] ?? '',
} as any;

describe('sparkDatatableLabels', () => {
  it('names a resize handle by the column label in the viewer language, not by its name', () => {
    const labels = sparkDatatableLabels(lang, [{ name: 'Created', label: { en: 'Added', fr: 'Ajoutée' } }]);
    expect(labels.resizeColumn('Created')).toBe('Redimensionner la colonne Ajoutée');
  });

  it('falls back to the name for a column without a label', () => {
    const labels = sparkDatatableLabels(lang, [{ name: 'Plain' }]);
    expect(labels.resizeColumn('Plain')).toBe('Redimensionner la colonne Plain');
  });

  it('labels a non-data column from the extra map', () => {
    const labels = sparkDatatableLabels(lang, [], { __sparkRowActions: 'Actions' });
    expect(labels.resizeColumn('__sparkRowActions')).toBe('Redimensionner la colonne Actions');
  });
});
