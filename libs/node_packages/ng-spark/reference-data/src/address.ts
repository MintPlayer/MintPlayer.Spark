import { ISO_3166_1_ALPHA_2 } from './iso-regions';

/**
 * The `Address` value object as the generic form holds it (PRD §4.15; C# `MintPlayer.Spark.ReferenceData.Address`):
 * one flat record keyed by the C# property names, which is what a single AsDetail value edits as (X15).
 */
export interface SparkAddress {
  CountryCode?: string | null;
  StreetId?: string | null;
  StreetName?: string | null;
  HouseNumber?: string | null;
  Box?: string | null;
  PostalCodeId?: string | null;
  PostalCode?: string | null;
  LocalityKey?: string | null;
  LocalityName?: string | null;
  LocalityPlaceId?: string | null;
  MunicipalityId?: string | null;
  MunicipalityName?: string | null;
  TimeZone?: string | null;
  Lines?: string[] | null;
  Release?: string | null;
  Verified?: boolean | null;
  HouseNumberVerified?: boolean | null;
}

/** The generic template when no profile is known (the C# `AddressLines.Fallback`). */
export const FALLBACK_ADDRESS_LINES: readonly string[] = ['{street} {houseNumber}[ {box}]', '{postalCode} {locality}'];

/**
 * The address's lines from a profile template: the TypeScript twin of the C# `AddressLines.Render`, so the form shows
 * what the server will store. `[ bus {box}]` is an optional part, dropped with its value; an empty line is dropped.
 */
export function renderAddressLines(address: SparkAddress | null | undefined, template?: readonly string[] | null): string[] {
  if (!address) return [];
  const values: Record<string, string | null | undefined> = {
    street: address.StreetName,
    houseNumber: address.HouseNumber,
    box: address.Box,
    postalCode: address.PostalCode,
    locality: address.LocalityName ?? address.MunicipalityName,
    municipality: address.MunicipalityName,
  };
  const has = (name: string) => !!values[name]?.toString().trim();
  const lines: string[] = [];
  for (const line of template && template.length > 0 ? template : FALLBACK_ADDRESS_LINES) {
    const withOptional = line.replace(/\[([^[\]]*)\]/g, (_, part: string) =>
      [...part.matchAll(/\{(\w+)\}/g)].every(m => has(m[1])) ? part : '');
    const anyValue = [...withOptional.matchAll(/\{(\w+)\}/g)].some(m => has(m[1]));
    const filled = withOptional.replace(/\{(\w+)\}/g, (_, name: string) => values[name]?.toString().trim() ?? '');
    const collapsed = filled.split(' ').filter(p => p.length > 0).join(' ').replace(/^[ ,]+|[ ,]+$/g, '');
    if (anyValue && collapsed.length > 0) lines.push(collapsed);
  }
  return lines;
}

/** Whether every field of the address is empty (an optional address nobody filled in is saved as null). */
export function isEmptyAddress(address: SparkAddress | null | undefined): boolean {
  return !address || !['CountryCode', 'StreetName', 'HouseNumber', 'PostalCode', 'LocalityName']
    .some(key => !!(address as Record<string, unknown>)[key]?.toString().trim());
}

export interface CountryChoice {
  code: string;
  name: string;
}

/**
 * The country picker's options: every ISO 3166-1 code named with `Intl.DisplayNames` in `language` and sorted with
 * `Intl.Collator` (K11). The `first` codes (the instance's countries) lead the list.
 */
export function countryChoices(language: string, first: readonly string[] = []): CountryChoice[] {
  let names: Intl.DisplayNames | null = null;
  try {
    names = new Intl.DisplayNames([language], { type: 'region' });
  } catch {
    names = null;
  }
  const collator = new Intl.Collator(language, { sensitivity: 'base' });
  const all = ISO_3166_1_ALPHA_2.map(code => ({ code, name: names?.of(code) ?? code }));
  const leading = first.map(c => c.toUpperCase()).filter(c => ISO_3166_1_ALPHA_2.includes(c));
  const rest = all.filter(c => !leading.includes(c.code)).sort((a, b) => collator.compare(a.name, b.name));
  return [...leading.map(code => all.find(c => c.code === code)!), ...rest];
}
