import { referenceDataBaseUrl, REFERENCE_DATA_DEV_URL } from './reference-data-url';

describe('referenceDataBaseUrl', () => {
  it('points the local development app at apps/ReferenceData', () => {
    expect(referenceDataBaseUrl({ hostname: 'localhost', port: '5015' })).toBe(REFERENCE_DATA_DEV_URL);
  });

  it('falls back to free text on any other origin (the E2E host, a deployment)', () => {
    expect(referenceDataBaseUrl({ hostname: 'localhost', port: '49152' })).toBeNull();
    expect(referenceDataBaseUrl({ hostname: 'festival.example', port: '' })).toBeNull();
  });
});
