import { Injectable, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

// Static imports for all icons used in the app
import arrowLeft from 'bootstrap-icons/icons/arrow-left.svg';
import building from 'bootstrap-icons/icons/building.svg';
import database from 'bootstrap-icons/icons/database.svg';
import file from 'bootstrap-icons/icons/file.svg';
import folder from 'bootstrap-icons/icons/folder.svg';
import pencil from 'bootstrap-icons/icons/pencil.svg';
import people from 'bootstrap-icons/icons/people.svg';
import plusLg from 'bootstrap-icons/icons/plus-lg.svg';
import search from 'bootstrap-icons/icons/search.svg';
import trash from 'bootstrap-icons/icons/trash.svg';
import xLg from 'bootstrap-icons/icons/x-lg.svg';

@Injectable({ providedIn: 'root' })
export class IconRegistry {
  private sanitizer = inject(DomSanitizer);
  private icons = new Map<string, SafeHtml>();

  constructor() {
    this.register('arrow-left', arrowLeft);
    this.register('building', building);
    this.register('database', database);
    this.register('file', file);
    this.register('folder', folder);
    this.register('pencil', pencil);
    this.register('people', people);
    this.register('plus-lg', plusLg);
    this.register('search', search);
    this.register('trash', trash);
    this.register('x-lg', xLg);
  }

  private register(name: string, svg: string): void {
    this.icons.set(name, this.sanitizer.bypassSecurityTrustHtml(svg));
  }

  get(name: string): SafeHtml | undefined {
    return this.icons.get(name);
  }

  has(name: string): boolean {
    return this.icons.has(name);
  }
}
