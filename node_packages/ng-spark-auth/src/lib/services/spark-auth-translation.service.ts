import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

type TranslatedString = Record<string, string>;

@Injectable({ providedIn: 'root' })
export class SparkAuthTranslationService {
  private readonly http = inject(HttpClient);
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.loadTranslations();
  }

  private async loadTranslations(): Promise<void> {
    try {
      const t = await firstValueFrom(this.http.get<Record<string, TranslatedString>>('/spark/translations'));
      this.translationsMap.set(t);
    } catch {
      // Translations failed to load; keys will be shown as-is
    }
  }

  t(key: string): string {
    const ts = this.translationsMap()[key];
    if (!ts) return key;
    const lang = localStorage.getItem('spark-lang') ?? navigator.language?.split('-')[0] ?? 'en';
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? key;
  }
}
