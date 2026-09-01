import { HttpInterceptorFn } from '@angular/common/http';
import { currentLanguage } from '@mintplayer/ng-spark/models';

/**
 * Sends the language the user picked to the server, as `Accept-Language`.
 *
 * `SparkLanguageService` keeps the choice in `localStorage` and never puts it on the wire: it is a
 * client-side concern, because almost everything translated reaches the client as a whole
 * `TranslatedString` and is resolved by the `resolveTranslation` pipe. Server-*resolved* strings
 * are the exception, and the composed Home page has two of them — the breadcrumb that becomes the
 * page's `<h2>`, and the welcome subtitle — because a `PersistentObject`'s breadcrumb and an
 * attribute's value are plain strings with nowhere to carry three languages.
 *
 * Spark resolves those through `IRequestCultureResolver`, which reads `Accept-Language`. Without
 * this the browser's header wins, so a visitor whose browser prefers Dutch sees a Dutch title
 * above an otherwise English page, and switching the language selector never changes it.
 *
 * ⚠️ Reads the module-level `currentLanguage` signal, and **must not** `inject(SparkLanguageService)`.
 * That service fetches `/culture` and `/translations` from its own constructor, so an interceptor
 * that injects it is re-entered while it is still being constructed: Angular throws NG0200 for
 * every request, the culture and translation payloads never arrive, and the whole UI renders raw
 * keys ("app.title") with the language selector missing. `currentLanguage` is the same value
 * without the cycle — the service writes it on load and on every change.
 *
 * Empty until `/culture` resolves, which is exactly the window where there is no user choice to
 * honour yet; those first requests fall through to the browser's own header.
 */
export const sparkLanguageInterceptor: HttpInterceptorFn = (req, next) => {
  const language = currentLanguage();
  if (!language) return next(req);
  return next(req.clone({ setHeaders: { 'Accept-Language': language } }));
};
