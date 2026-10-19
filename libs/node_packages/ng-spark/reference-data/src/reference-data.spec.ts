import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { firstValueFrom } from 'rxjs';
import { countryChoices, isEmptyAddress, renderAddressLines } from './address';
import { ISO_3166_1_ALPHA_2 } from './iso-regions';
import { displayPlaceName } from './place-names';
import { ReferenceDataApi } from './reference-data-api.service';
import { provideReferenceData } from './reference-data.config';
import { normalizeBaseUrl } from './reference-data.token';

const be = ['{street} {houseNumber}[ bus {box}]', '{postalCode} {locality}'];

describe('renderAddressLines (the twin of the C# AddressLines.Render)', () => {
  it('renders the Belgian template with a box', () => {
    expect(renderAddressLines({ StreetName: 'Kerkstraat', HouseNumber: '12', Box: '3', PostalCode: '9770', LocalityName: 'Kruishoutem' }, be))
      .toEqual(['Kerkstraat 12 bus 3', '9770 Kruishoutem']);
  });

  it('drops the optional part with its empty value', () => {
    expect(renderAddressLines({ StreetName: 'Kerkstraat', HouseNumber: '12', PostalCode: '9770', LocalityName: 'Kruishoutem' }, be))
      .toEqual(['Kerkstraat 12', '9770 Kruishoutem']);
  });

  it('drops a line with no value at all and falls back to the municipality', () => {
    expect(renderAddressLines({ PostalCode: '9770', MunicipalityName: 'Kruisem' }, be)).toEqual(['9770 Kruisem']);
  });

  it('uses the generic template without a profile', () => {
    expect(renderAddressLines({ StreetName: 'Rue de la Loi', HouseNumber: '16', PostalCode: '1000', LocalityName: 'Bruxelles' }))
      .toEqual(['Rue de la Loi 16', '1000 Bruxelles']);
  });

  it('treats an address with only blank fields as empty', () => {
    expect(isEmptyAddress({ CountryCode: ' ', StreetName: '' })).toBe(true);
    expect(isEmptyAddress({ CountryCode: 'BE' })).toBe(false);
  });
});

describe('displayPlaceName (the twin of the C# PlaceNames.Display)', () => {
  const brussels = [
    { tag: 'fr', value: 'Rue de la Loi', kind: 'official' },
    { tag: 'nl', value: 'Wetstraat', kind: 'official' },
  ];
  const antwerpen = [
    { tag: 'nl', value: 'Antwerpen', kind: 'official' },
    { tag: 'fr', value: 'Anvers', kind: 'exonym' },
  ];

  it('joins several official names in the authority order (Q-R13)', () => {
    expect(displayPlaceName(brussels, ['fr', 'nl'], 'en', '2026-10-19').display).toBe('Rue de la Loi - Wetstraat');
  });

  it('shows the official name in the UI language when it is official there', () => {
    expect(displayPlaceName(brussels, ['fr', 'nl'], 'nl-BE', '2026-10-19').display).toBe('Wetstraat');
  });

  it('adds the exonym after the local name otherwise', () => {
    expect(displayPlaceName(antwerpen, ['nl'], 'fr', '2026-10-19').display).toBe('Antwerpen (Anvers)');
  });

  it('honours validity: a renamed place shows its name of that date', () => {
    const sambreville = [
      { tag: 'fr', value: 'Basse-Sambre', kind: 'official', validFrom: '1977-01-01', validTo: '1979-01-01' },
      { tag: 'fr', value: 'Sambreville', kind: 'official', validFrom: '1979-01-01' },
    ];
    expect(displayPlaceName(sambreville, ['fr'], 'fr', '1978-06-01').display).toBe('Basse-Sambre');
    expect(displayPlaceName(sambreville, ['fr'], 'fr', '2026-10-19').display).toBe('Sambreville');
  });
});

describe('the country list', () => {
  it('holds the 249 ISO 3166-1 codes', () => {
    expect(ISO_3166_1_ALPHA_2.length).toBe(249);
    expect(new Set(ISO_3166_1_ALPHA_2).size).toBe(249);
    expect(ISO_3166_1_ALPHA_2).not.toContain('EU');
    expect(ISO_3166_1_ALPHA_2).not.toContain('XK');
  });

  it('leads with the preferred countries and names them with Intl.DisplayNames', () => {
    const choices = countryChoices('nl', ['BE', 'NL']);
    expect(choices.slice(0, 2).map(c => c.code)).toEqual(['BE', 'NL']);
    expect(choices[0].name).toBe(new Intl.DisplayNames(['nl'], { type: 'region' }).of('BE'));
    expect(choices.length).toBe(249);
  });
});

describe('configuration', () => {
  it('accepts only absolute http(s) base URLs, without the trailing slash', () => {
    expect(normalizeBaseUrl('https://refdata.example.org/')).toBe('https://refdata.example.org');
    expect(normalizeBaseUrl('/relative')).toBeNull();
    expect(normalizeBaseUrl('javascript:alert(1)')).toBeNull();
    expect(normalizeBaseUrl(null)).toBeNull();
  });

  it('free-text mode answers without a request', async () => {
    TestBed.configureTestingModule({ providers: [provideReferenceData({ baseUrl: null })] });
    const api = TestBed.inject(ReferenceDataApi);
    expect(api.enabled).toBe(false);
    expect(await firstValueFrom(api.suggest('kruish'))).toBeNull();
    expect(await api.timeZoneNames('nl')).toEqual({});
  });

  it('fails loudly without provideReferenceData (no default URL)', () => {
    TestBed.configureTestingModule({ providers: [] });
    expect(() => TestBed.inject(ReferenceDataApi)).toThrow(/REFERENCE_DATA_CONFIG|NG0201/);
  });
});
