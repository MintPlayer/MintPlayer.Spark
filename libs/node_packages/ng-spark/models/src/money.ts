import { currentLanguage } from './translated-string';

/**
 * A Spark `Money` value (commerce X4) as it travels: an amount in one ISO 4217 currency.
 *
 * The member names are the CLR names, `Amount` and `Currency`, whatever the API's naming policy: the
 * server stores and sends one shape, so that `x.Price.Amount` in a RavenDB query addresses the stored
 * field. {@link asMoney} also reads the camel-cased spelling, for values a host builds by hand.
 *
 * The server is exact: it refuses an amount with more decimals than the currency has (12.345 EUR), and
 * never rounds silently. The client therefore only formats and edits; it does no money arithmetic.
 */
export interface Money {
  Amount: number;
  Currency: string;
}

/** The model `dataType` of a Money attribute. */
export const MONEY_DATA_TYPE = 'Money';

/** The core renderer model sync writes on a Money attribute that has none. */
export const MONEY_RENDERER = 'money';

/**
 * Reads a value as {@link Money}: `{ Amount, Currency }` (or `{ amount, currency }`), with the amount a
 * number or a numeric string. Anything else, `null` included, is `null`.
 */
export function asMoney(value: unknown): Money | null {
  if (value === null || typeof value !== 'object') return null;
  const record = value as Record<string, unknown>;
  const rawAmount = record['Amount'] ?? record['amount'];
  const rawCurrency = record['Currency'] ?? record['currency'];
  const amount = typeof rawAmount === 'number' ? rawAmount
    : typeof rawAmount === 'string' && rawAmount.trim() !== '' ? Number(rawAmount)
    : NaN;
  if (!Number.isFinite(amount) || typeof rawCurrency !== 'string' || rawCurrency.trim() === '') return null;
  return { Amount: amount, Currency: rawCurrency.trim().toUpperCase() };
}

/**
 * The locale money is formatted in: the viewer's own regional variant of the UI language when the browser
 * has one (`nl-BE` for a Belgian browser on a Dutch UI), else the UI language itself. Formatting follows
 * the language the viewer reads the page in, not the server's culture.
 */
export function viewerLocale(): string {
  const language = currentLanguage();
  const primary = language.toLowerCase().split('-')[0];
  const preferred = typeof navigator === 'undefined' ? [] : (navigator.languages ?? [navigator.language]);
  return preferred.find(tag => !!tag && tag.toLowerCase().split('-')[0] === primary) ?? language;
}

/**
 * How many decimals a currency is shown with, per the platform's CLDR data (`Intl.NumberFormat`): 2 for
 * EUR, 0 for JPY. Falls back to 2 for a code the platform does not know.
 */
export function currencyFractionDigits(currency: string): number {
  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency }).resolvedOptions().maximumFractionDigits ?? 2;
  } catch {
    return 2;
  }
}

/** The number of decimals a JS number actually carries (12.5 → 1, 12 → 0). */
function decimalsOf(amount: number): number {
  const text = String(Math.abs(amount));
  if (text.includes('e-')) return Number(text.split('e-')[1]) + (text.split('e-')[0].split('.')[1]?.length ?? 0);
  return text.split('.')[1]?.length ?? 0;
}

/**
 * Formats a Money value with `Intl.NumberFormat` (K11: the standard API, no library), in the viewer's
 * locale: `€ 12,30` for `nl-BE`, `€12.30` for `en-US`, `¥1,230` for JPY.
 *
 * Never hides precision: CLDR shows some currencies with fewer decimals than ISO 4217 gives them (IQD,
 * for one), so an amount that carries more decimals than the platform's default is shown with all of
 * them rather than rounded on screen. Returns `''` for anything that is not money; a currency the
 * platform cannot format falls back to `12.3 XYZ`.
 */
export function formatMoney(value: unknown, locale: string | string[] = viewerLocale()): string {
  const money = asMoney(value);
  if (!money) return '';
  try {
    const base = new Intl.NumberFormat(locale, { style: 'currency', currency: money.Currency });
    const resolved = base.resolvedOptions();
    const present = decimalsOf(money.Amount);
    if (present <= (resolved.maximumFractionDigits ?? 0)) return base.format(money.Amount);
    return new Intl.NumberFormat(locale, {
      style: 'currency',
      currency: money.Currency,
      minimumFractionDigits: resolved.minimumFractionDigits,
      maximumFractionDigits: Math.min(present, 20),
    }).format(money.Amount);
  } catch {
    return `${money.Amount} ${money.Currency}`;
  }
}

/**
 * The currency codes an editor offers: the configured list when there is one, else every code the
 * platform knows (`Intl.supportedValuesOf('currency')`), with the value's own and the default currency
 * always included so an existing value can be shown and kept.
 */
export function currencyChoices(configured: readonly string[] | undefined, ...always: (string | null | undefined)[]): string[] {
  const supported: string[] = configured?.length
    ? [...configured]
    : ((Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.('currency') ?? []);
  const codes = new Set(supported.map(code => code.toUpperCase()));
  for (const code of always) if (code) codes.add(code.toUpperCase());
  return [...codes].sort();
}

/** A currency's localized name (`Intl.DisplayNames`), or the code itself where the platform has none. */
export function currencyDisplayName(currency: string, locale: string = viewerLocale()): string {
  try {
    return new Intl.DisplayNames([locale], { type: 'currency' }).of(currency) ?? currency;
  } catch {
    return currency;
  }
}
