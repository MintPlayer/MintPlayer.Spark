import { Injectable, inject, signal, type Signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

import { resolveTranslation, type TranslatedString } from '@mintplayer/ng-spark/models';

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
    return resolveTranslation(ts, currentLanguage()) || key;
  }
}
