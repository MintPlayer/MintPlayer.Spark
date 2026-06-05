import { Injectable, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { SPARK_BUILT_IN_ICONS } from './spark-built-in-icons';

/**
 * R2-M12: SVG icon registry. The registry no longer accepts a raw string —
 * apps must pre-sanitize via `DomSanitizer.bypassSecurityTrustHtml` (or
 * `parseSvg` to validate) before calling `register`. Treating the parameter
 * as `SafeHtml` puts the explicit bypass decision in the caller's hands so a
 * developer wiring server-supplied SVG (per-tenant branding fetched from a
 * JSON endpoint) doesn't accidentally trust a `<svg><script>...</script>`
 * payload.
 *
 * The built-in icons baked into the package are still bypass-trusted internally
 * because they're known-safe at build time.
 */
@Injectable({ providedIn: 'root' })
export class SparkIconRegistry {
  private sanitizer = inject(DomSanitizer);
  private icons = new Map<string, SafeHtml>();

  constructor() {
    for (const [name, svg] of Object.entries(SPARK_BUILT_IN_ICONS)) {
      this.icons.set(name, this.sanitizer.bypassSecurityTrustHtml(svg));
    }
  }

  /**
   * Registers a pre-sanitized SVG icon. Callers MUST validate or explicitly
   * trust the SVG before passing — for example:
   *
   * ```ts
   * const safe = sanitizer.bypassSecurityTrustHtml(svgFromBuildTimeConstant);
   * registry.register('app-logo', safe);
   * ```
   *
   * For server-supplied SVG, prefer a strict allow-list parser (no `<script>`,
   * no `on*` attributes, no `href="javascript:..."`) before trusting.
   */
  register(name: string, svg: SafeHtml): void {
    this.icons.set(name, svg);
  }

  get(name: string): SafeHtml | undefined {
    return this.icons.get(name);
  }

  has(name: string): boolean {
    return this.icons.has(name);
  }
}
