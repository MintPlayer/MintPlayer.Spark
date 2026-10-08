import { Injectable, PLATFORM_ID, inject, signal } from '@angular/core';
import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TranslatedString, currentLanguage } from '@mintplayer/ng-spark/models';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

/** One year, in seconds: the `spark-lang` cookie outlives sessions like the localStorage entry it mirrors. */
const LANGUAGE_COOKIE_MAX_AGE = 365 * 24 * 60 * 60;

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
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly currentLang = signal('en');
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  readonly language = this.currentLang.asReadonly();
  readonly languages = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.loadCulture();
    this.loadTranslations();
  }

  private async loadCulture(): Promise<void> {
    const config = await firstValueFrom(this.http.get<CultureConfiguration>(`${this.baseUrl}/culture`));
    this.languages.set(config.languages);
    const saved = localStorage.getItem('spark-lang');
    const lang = saved ?? config.defaultLanguage;
    this.currentLang.set(lang);
    currentLanguage.set(lang);
    this.writeCookie(lang);
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
    localStorage.setItem('spark-lang', lang);
    this.writeCookie(lang);
  }

  resolve(ts: TranslatedString | undefined): string {
    if (!ts) return '';
    const lang = this.currentLang();
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
  }

  t(key: string): string {
    const ts = this.translationsMap()[key];
    return this.resolve(ts) || key;
  }
}
