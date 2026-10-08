/**
 * Shared validator for `returnUrl` query parameters consumed by the auth
 * components and interceptor. The framework-side equivalent in
 * `SparkAuthenticationExtensions.SanitizeReturnUrl` enforces the same rule;
 * keep them in sync.
 *
 * Accepts only local in-app paths:
 * - Must start with a single `/`.
 * - Must not start with `//` or `/\` (protocol-relative navigation that some
 *   browsers and routers interpret as an external host).
 * - Must not contain CR/LF (defensive — the value flows into Router state and
 *   eventually back into a `router.navigateByUrl` call).
 *
 * Returns the supplied default for anything else, never throws. Per R2-H9.
 */
export function isSafeReturnUrl(value: string | null | undefined): boolean {
  if (!value) return false;
  if (/[\r\n]/.test(value)) return false;
  if (!value.startsWith('/')) return false;
  if (value.length >= 2 && (value[1] === '/' || value[1] === '\\')) return false;
  return true;
}

export function sanitizeReturnUrl(
  value: string | null | undefined,
  defaultUrl: string,
): string {
  return isSafeReturnUrl(value) ? (value as string) : defaultUrl;
}
