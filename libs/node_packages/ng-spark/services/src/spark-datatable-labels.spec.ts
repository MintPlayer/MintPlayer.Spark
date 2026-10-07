import { describe, expect, it } from 'vitest';
import { sparkDatatableLabels } from './spark-datatable-labels';

const translations: Record<string, string> = {
  'common.resizeColumn': 'Redimensionner la colonne {column}',
  'common.resizeColumnHint': 'Les flèches gauche et droite redimensionnent la colonne.',
  'common.resizeColumnOptions': 'Options de redimensionnement pour {column}',
  'common.narrowerColumn': 'Rendre {column} plus étroite',
  'common.widerColumn': 'Rendre {column} plus large',
  'common.columnWidth': '{px} px',
  'common.fitColumn': 'Ajuster au contenu',
  'common.resetColumn': 'Réinitialiser la largeur',
};
const lang = { t: (key: string) => translations[key] ?? key } as any;

describe('sparkDatatableLabels', () => {
  it('fills the column into each translated resize formatter', () => {
    const labels = sparkDatatableLabels(lang);
    expect(labels.resizeColumn('Ajoutée')).toBe('Redimensionner la colonne Ajoutée');
    expect(labels.resizeColumnOptions('Ajoutée')).toBe('Options de redimensionnement pour Ajoutée');
    expect(labels.narrowerColumn('Ajoutée')).toBe('Rendre Ajoutée plus étroite');
    expect(labels.widerColumn('Ajoutée')).toBe('Rendre Ajoutée plus large');
    expect(labels.columnWidth(120)).toBe('120 px');
  });

  it('translates the keymap announcement and the dialog buttons', () => {
    const labels = sparkDatatableLabels(lang);
    expect(labels.resizeColumnHint).toBe('Les flèches gauche et droite redimensionnent la colonne.');
    expect(labels.fitColumn).toBe('Ajuster au contenu');
    expect(labels.resetColumn).toBe('Réinitialiser la largeur');
  });
});
