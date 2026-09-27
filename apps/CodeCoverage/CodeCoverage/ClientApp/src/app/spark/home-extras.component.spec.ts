import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { AccountsService } from '../services/accounts.service';
import { settle } from '../../testing/test-utils';
import { HomeExtrasComponent } from './home-extras.component';

describe('HomeExtrasComponent', () => {
  async function setup(getMyAccounts: () => Promise<unknown>, signedIn: boolean) {
    const user = signal<{ isAuthenticated: boolean } | null>(signedIn ? { isAuthenticated: true } : null);
    const accounts = { getMyAccounts: vi.fn(getMyAccounts) };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideNoopAnimations(),
        provideRouter([]),
        { provide: AccountsService, useValue: accounts },
        { provide: SparkAuthService, useValue: { user } },
        { provide: SparkLanguageService, useValue: { t: (key: string) => key } },
      ],
    });
    const fixture = TestBed.createComponent(HomeExtrasComponent);
    await settle(fixture);
    return { fixture, user, accounts };
  }

  function el(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('shows neither the banner nor the hint, and fetches nothing, for an anonymous visitor', async () => {
    const { fixture, accounts } = await setup(() => Promise.resolve({ connectUrl: 'x', accounts: [] }), false);

    expect(accounts.getMyAccounts).not.toHaveBeenCalled();
    expect(el(fixture).querySelector('bs-alert')).toBeNull();
    expect(el(fixture).querySelector('p')).toBeNull();
  });

  it('points the install hint at the per-environment connect URL from /api/me/accounts', async () => {
    const { fixture } = await setup(
      () => Promise.resolve({ connectUrl: 'https://forge.example.test/apps/coverage-staging', accounts: [] }), true);

    const link = el(fixture).querySelector('p a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://forge.example.test/apps/coverage-staging');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener');
    expect(el(fixture).querySelector('bs-alert')).toBeNull();
  });

  it('shows the reconnect banner, linking to /sign-in, when a forge credential is dead', async () => {
    const { fixture } = await setup(() => Promise.resolve({ connectUrl: 'x', accounts: [], reauthRequired: true }), true);

    const alert = el(fixture).querySelector('bs-alert')!;
    expect(alert).not.toBeNull();
    expect(alert.textContent).toContain('app.reauthBanner');
    expect(alert.querySelector('a')!.getAttribute('href')).toBe('/sign-in');
  });

  // The banner is an escalation: when we cannot tell, say nothing, and keep the default hint URL.
  it('stays quiet and keeps the default connect URL when the lookup fails', async () => {
    const { fixture } = await setup(() => Promise.reject(new Error('500')), true);

    expect(el(fixture).querySelector('bs-alert')).toBeNull();
    expect(fixture.componentInstance.reauthRequired()).toBe(false);
    expect(el(fixture).querySelector('p a')!.getAttribute('href')).toBe('https://github.com/apps/coverageproduction');
  });

  it('treats an absent reauthRequired as false', async () => {
    const { fixture } = await setup(() => Promise.resolve({ connectUrl: 'x', accounts: [] }), true);

    expect(fixture.componentInstance.reauthRequired()).toBe(false);
  });

  it('clears the banner on sign-out', async () => {
    const { fixture, user } = await setup(() => Promise.resolve({ connectUrl: 'x', accounts: [], reauthRequired: true }), true);
    expect(el(fixture).querySelector('bs-alert')).not.toBeNull();

    user.set(null);
    await settle(fixture);

    expect(el(fixture).querySelector('bs-alert')).toBeNull();
    expect(el(fixture).querySelector('p')).toBeNull();
  });
});
