import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { sparkLanguageInterceptor } from './spark-language.interceptor';

describe('sparkLanguageInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  // currentLanguage is a process-wide signal (globalThis), so put it back for the next spec.
  const initial = currentLanguage();

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([sparkLanguageInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    controller.verify();
    currentLanguage.set(initial);
  });

  it('sends the picked language as Accept-Language', async () => {
    currentLanguage.set('nl');

    const response = firstValueFrom(http.get('/spark/po/home/main'));
    const req = controller.expectOne('/spark/po/home/main');
    expect(req.request.headers.get('Accept-Language')).toBe('nl');
    req.flush({});
    await response;
  });

  it('follows a language change on the next request', async () => {
    currentLanguage.set('fr');
    const first = firstValueFrom(http.get('/a'));
    controller.expectOne('/a').flush({});
    await first;

    currentLanguage.set('en');
    const second = firstValueFrom(http.get('/b'));
    const req = controller.expectOne('/b');
    expect(req.request.headers.get('Accept-Language')).toBe('en');
    req.flush({});
    await second;
  });

  // Before /culture resolves there is no choice to honour; the browser's own header must win.
  it('leaves the request untouched while no language is known', async () => {
    currentLanguage.set('');

    const response = firstValueFrom(http.get('/spark/culture'));
    const req = controller.expectOne('/spark/culture');
    expect(req.request.headers.has('Accept-Language')).toBe(false);
    req.flush({});
    await response;
  });
});
