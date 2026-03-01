import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TranslatedString } from '../models';
import { SPARK_CONFIG } from '../models/spark-config';

interface CultureConfiguration {
  languages: Record<string, TranslatedString>;
  defaultLanguage: string;
}

@Injectable({ providedIn: 'root' })
export class SparkLanguageService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SPARK_CONFIG, { optional: true });
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
    this.currentLang.set(saved ?? config.defaultLanguage);
  }

  private async loadTranslations(): Promise<void> {
    const t = await firstValueFrom(this.http.get<Record<string, TranslatedString>>(`${this.baseUrl}/translations`));
    this.translationsMap.set(t);
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
