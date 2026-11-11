import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkLanguageSelectorComponent } from './spark-language-selector.component';
import { settle } from '../../src/test-utils';

@Component({ standalone: true, template: '' })
class Page {}

/** The picker (#604 D9): real links to the current page in each language, so a crawler reaches every language. */
describe('SparkLanguageSelectorComponent', () => {
  async function render(languages: Record<string, Record<string, string>>, url = '/') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: Page }]),
        {
          provide: SparkLanguageService,
          useValue: { languages: signal(languages), language: signal('en'), t: (_: string, fallback: string) => fallback },
        },
      ],
    });
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(SparkLanguageSelectorComponent);
    await settle(fixture);
    return fixture.nativeElement as HTMLElement;
  }

  it('renders nothing for one language', async () => {
    const el = await render({ en: { en: 'English' } });

    expect(el.querySelector('details')).toBeNull();
  });

  it('links the current page, its query kept, in every language', async () => {
    const el = await render({ en: { en: 'English' }, nl: { en: 'Dutch' } }, '/query/songs?page=2');

    const links = Array.from(el.querySelectorAll('a'));
    expect(links.map(a => a.getAttribute('href'))).toEqual(['/query/songs?page=2&lang=en', '/query/songs?page=2&lang=nl']);
    expect(links.map(a => a.getAttribute('hreflang'))).toEqual(['en', 'nl']);
    expect(links.map(a => a.textContent!.trim())).toEqual(['English', 'Dutch']);
    expect(links[0].getAttribute('aria-current')).toBe('true');
    expect(el.querySelector('summary')!.textContent!.trim()).toBe('EN');
  });

  it('closes the menu when a language is picked', async () => {
    const el = await render({ en: { en: 'English' }, nl: { en: 'Dutch' } });
    const details = el.querySelector('details')!;
    details.open = true;

    el.querySelectorAll('a')[1].click();

    expect(details.open).toBe(false);
  });
});
