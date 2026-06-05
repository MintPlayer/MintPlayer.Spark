import { Injectable, inject, signal, type Signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

type TranslatedString = Record<string, string>;

/** Read the global language signal set by @mintplayer/ng-spark */
const currentLanguage: Signal<string> =
  (globalThis as any).__sparkCurrentLanguage ?? signal('en');

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
    const lang = currentLanguage();
    return ts[lang] ?? ts['en'] ?? Object.values(ts)[0] ?? key;
  }
}
