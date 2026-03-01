import { Injectable, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

@Injectable({ providedIn: 'root' })
export class SparkIconRegistry {
  private sanitizer = inject(DomSanitizer);
  private icons = new Map<string, SafeHtml>();

  register(name: string, svg: string): void {
    this.icons.set(name, this.sanitizer.bypassSecurityTrustHtml(svg));
  }

  get(name: string): SafeHtml | undefined {
    return this.icons.get(name);
  }

  has(name: string): boolean {
    return this.icons.has(name);
  }
}
