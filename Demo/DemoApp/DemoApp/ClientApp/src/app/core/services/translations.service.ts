import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TranslatedString, resolveTranslation } from '../models';

@Injectable({ providedIn: 'root' })
export class TranslationsService {
  private readonly http = inject(HttpClient);
  private readonly translationsMap = signal<Record<string, TranslatedString>>({});

  constructor() {
    this.http.get<Record<string, TranslatedString>>('/spark/translations').subscribe(t => {
      this.translationsMap.set(t);
    });
  }

  t(key: string): string {
    const ts = this.translationsMap()[key];
    return resolveTranslation(ts) || key;
  }
}
