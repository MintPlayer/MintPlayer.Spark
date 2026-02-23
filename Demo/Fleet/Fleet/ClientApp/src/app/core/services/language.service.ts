import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TranslatedString } from '../models';

interface CultureConfiguration {
  languages: Record<string, TranslatedString>;
  defaultLanguage: string;
}

@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly http = inject(HttpClient);
  private readonly currentLang = signal('en');
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  readonly language = this.currentLang.asReadonly();
  readonly languages = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.http.get<CultureConfiguration>('/spark/culture').subscribe(config => {
      this.languages.set(config.languages);
      const saved = localStorage.getItem('spark-lang');
      this.currentLang.set(saved ?? config.defaultLanguage);
    });
    this.http.get<Record<string, TranslatedString>>('/spark/translations').subscribe(t => {
      this.translationsMap.set(t);
    });
  }

  setLanguage(lang: string) {
    this.currentLang.set(lang);
    localStorage.setItem('spark-lang', lang);
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
