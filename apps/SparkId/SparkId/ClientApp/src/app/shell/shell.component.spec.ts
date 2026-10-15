import { NO_ERRORS_SCHEMA, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import { SparkService } from '@mintplayer/ng-spark/services';
import { ShellComponent } from './shell.component';

describe('ShellComponent', () => {
  it('gives the identity provider links the shape of the menu\'s own (D84)', async () => {
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkService, useValue: { getProgramUnits: async () => ({ programUnitGroups: [] }) } },
        { provide: SparkAuthService, useValue: { isAuthenticated: signal(true) } },
        { provide: SparkAuthTranslationService, useValue: { t: (key: string) => key } },
      ],
    });
    // The auth bar has its own spec; this one looks at the sidebar.
    TestBed.overrideComponent(ShellComponent, { remove: { imports: [SparkAuthBarComponent] }, add: { schemas: [NO_ERRORS_SCHEMA] } });
    const fixture = TestBed.createComponent(ShellComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // No Bootstrap .nav/.nav-link rules ship with what the sidebar uses: spark-shell colours a .nav-link there, and the
    // utilities give it the menu's shape. A bare .nav-link was an underlined inline link, all three on one line.
    const sidebar = (fixture.nativeElement as HTMLElement).querySelector('nav.spark-sidebar')!;
    expect(sidebar.querySelectorAll('nav.app-idp-links > a').length).toBe(3);
    expect(sidebar.querySelectorAll('nav.app-idp-links > a.d-block.px-3.py-2.text-decoration-none.nav-link').length).toBe(3);
  });
});
