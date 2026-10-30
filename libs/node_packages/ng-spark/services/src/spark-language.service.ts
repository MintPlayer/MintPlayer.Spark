import { Injectable, PLATFORM_ID, effect, inject, signal } from '@angular/core';
import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TranslatedString, currentLanguage } from '@mintplayer/ng-spark/models';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SPARK_SERVER_LANGUAGE } from './provide-spark-hydration';

/** One year, in seconds: the `spark-lang` cookie, the source of truth for the language, outlives sessions. */
const LANGUAGE_COOKIE_MAX_AGE = 365 * 24 * 60 * 60;

/** The language a viewer picked before the cookie became the source of truth, if any. */
function readStoredLanguage(): string | null {
  try { return localStorage.getItem('spark-lang'); } catch { return null; }
}

interface CultureConfiguration {
  languages: Record<string, TranslatedString>;
  defaultLanguage: string;
}

@Injectable({ providedIn: 'root' })
export class SparkLanguageService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SPARK_CONFIG, { optional: true });
  /** Null outside a browser (server-side rendering), where there is no cookie to write. */
  private readonly document = isPlatformBrowser(inject(PLATFORM_ID)) ? inject(DOCUMENT) : null;
  private readonly serverLanguage = inject(SPARK_SERVER_LANGUAGE, { optional: true });
  /** The page document on both platforms: `<html lang>` follows the language. */
  private readonly pageDocument = inject(DOCUMENT);
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly currentLang = signal('en');
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  readonly language = this.currentLang.asReadonly();

  /** Mirrors the language into `<html lang>`, on the server (the render's language) and in the browser. */
  private readonly htmlLang = effect(() => this.pageDocument.documentElement?.setAttribute('lang', this.currentLang()));

  readonly languages = signal<Record<string, TranslatedString>>({});

  constructor() {
    // A server render knows its language before /culture answers, and must render in it even if that
    // request was not supplied.
    if (this.serverLanguage) this.currentLang.set(this.serverLanguage);
    // Caught: a failed load leaves the defaults, and an unhandled rejection would end the server
    // renderer's whole Node process (SpaServices runs one for every request).
    this.loadCulture().catch(() => undefined);
    this.loadTranslations().catch(() => undefined);
  }

  private async loadCulture(): Promise<void> {
    const config = await firstValueFrom(this.http.get<CultureConfiguration>(`${this.baseUrl}/culture`));
    this.languages.set(config.languages);
    if (this.serverLanguage) {
      // Server render: the request's language, resolved by the .NET side from the same cookie the
      // browser reads below. The process-wide currentLanguage is left alone (see activeLanguage).
      this.currentLang.set(this.serverLanguage);
      return;
    }
    // The cookie is the source of truth, because it is what a server render sees: preferring
    // localStorage would hydrate a page in one language and switch it to another. localStorage is
    // read only for a viewer whose choice predates the cookie.
    const saved = this.readCookie() ?? readStoredLanguage();
    const lang = saved ?? config.defaultLanguage;
    this.currentLang.set(lang);
    currentLanguage.set(lang);
    this.writeCookie(lang);
  }

  private readCookie(): string | null {
    for (const part of this.document?.cookie.split(';') ?? []) {
      const [name, ...value] = part.trim().split('=');
      if (name === 'spark-lang' && value.length) {
        try { return decodeURIComponent(value.join('=')) || null; } catch { return null; }
      }
    }
    return null;
  }

  /**
   * Mirrors the language into the `spark-lang` cookie, which the server-rendered pages read
   * (`/connect/*` of the identity provider, `ConnectText.CultureCookie`): they cannot see
   * localStorage, and without the cookie a sign-in popup would open in the browser's language
   * rather than the one the user picked in the app. Not `HttpOnly` by nature (script writes it);
   * it carries only a language code, which the server matches against its supported cultures.
   */
  private writeCookie(lang: string): void {
    const document = this.document;
    if (!document || !lang) return;
    const secure = document.location?.protocol === 'https:' ? '; Secure' : '';
    document.cookie = `spark-lang=${encodeURIComponent(lang)}; Path=/; Max-Age=${LANGUAGE_COOKIE_MAX_AGE}; SameSite=Lax${secure}`;
  }

  private async loadTranslations(): Promise<void> {
    const t = await firstValueFrom(this.http.get<Record<string, TranslatedString>>(`${this.baseUrl}/translations`));
    this.translationsMap.set(t);
  }

  setLanguage(lang: string) {
    this.currentLang.set(lang);
    currentLanguage.set(lang);
    this.writeCookie(lang);
  }

  resolve(ts: TranslatedString | undefined): string {
    if (!ts) return '';
    const lang = this.currentLang();
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
  }

  /**
   * The text of `key` in the current language. When there is none (unknown key, empty text, or the
   * translations have not arrived yet) it is `fallback` when given, else the key itself, so
   * `t('spark.query.unavailable', 'This list is not available.')` never shows a raw key to a user.
   */
  t(key: string, fallback?: string): string {
    const ts = this.translationsMap()[key];
    return this.resolve(ts) || fallback || key;
  }
}
