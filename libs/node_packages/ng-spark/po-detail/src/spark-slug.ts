/**
 * The URL slug for a breadcrumb: lower case, accents dropped, every run of other characters one dash.
 * "Beyoncé - Halo!" → "beyonce-halo". Empty when nothing is left.
 */
export function sparkSlug(text: string | null | undefined): string {
  return (text ?? '')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}
