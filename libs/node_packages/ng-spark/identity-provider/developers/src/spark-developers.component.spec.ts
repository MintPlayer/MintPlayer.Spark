import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import { SparkDeveloperStatus, SparkIdentityProviderPortalService } from '@mintplayer/ng-spark/identity-provider/core';
import { SparkDevelopersComponent } from './spark-developers.component';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

function developer(extra: Partial<SparkDeveloperStatus> = {}): SparkDeveloperStatus {
  return {
    status: null, acceptedTermsVersion: null, currentTermsVersion: 2, termsUrl: 'https://example.test/terms',
    requireApproval: true, isActive: false, requestedAt: null, decidedAt: null, note: null, ...extra,
  };
}

const ok = <T>(value: T) => ({ ok: true as const, value });
const failed = (status: number) => ({ ok: false as const, status, problem: null });

type Portal = { developerStatus: ReturnType<typeof vi.fn>; requestDeveloperStatus: ReturnType<typeof vi.fn>; issueRegistrationToken: ReturnType<typeof vi.fn> };

async function render(portal: Partial<Portal>, secrets = { show: vi.fn() }) {
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkIdentityProviderPortalService, useValue: portal },
      { provide: SparkSecretDialogService, useValue: secrets },
      // The key, with its arguments, is the text: what is shown is then readable in the assertions.
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k.replace('identityProvider.spa.', '') + '|{0}' } },
    ],
  });
  const fixture = TestBed.createComponent(SparkDevelopersComponent);
  await settle(fixture);
  return { fixture, component: fixture.componentInstance, secrets };
}

async function settle(fixture: ComponentFixture<unknown>) {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
}

const html = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
const button = (fixture: ComponentFixture<unknown>, css: string) => html(fixture).querySelector<HTMLButtonElement>(css);

describe('SparkDevelopersComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('someone who never asked sees the terms, and asks once they accepted them', async () => {
    const requested = developer({ status: 'Requested', acceptedTermsVersion: 2, requestedAt: '2026-11-01T10:00:00Z' });
    const portal = {
      developerStatus: vi.fn().mockResolvedValue(ok(developer())),
      requestDeveloperStatus: vi.fn().mockResolvedValue(ok(requested)),
    };
    const { fixture, component } = await render(portal);

    expect(html(fixture).querySelector('.spark-developer-status')!.getAttribute('data-status')).toBe('none');
    expect(html(fixture).querySelector('.spark-developer-terms')!.getAttribute('href')).toBe('https://example.test/terms');
    expect(button(fixture, '.spark-developer-request')!.disabled).toBe(true);

    await component.request();
    expect(portal.requestDeveloperStatus).not.toHaveBeenCalled();

    component.accepted.set(true);
    await settle(fixture);
    button(fixture, '.spark-developer-request')!.click();
    await settle(fixture);

    expect(portal.requestDeveloperStatus).toHaveBeenCalledWith(2);
    expect(component.notice()).toBe('dev.requestSent|{0}');
    expect(component.accepted()).toBe(false);
    expect(html(fixture).querySelector('.spark-developer-request')).toBeNull();
    expect(html(fixture).querySelector('.spark-developer-status')!.getAttribute('data-status')).toBe('Requested');
  });

  it('an approval without review says so, a raised terms version reloads, a revocation and other failures are named', async () => {
    const portal = {
      developerStatus: vi.fn().mockResolvedValue(ok(developer({ status: 'Rejected', note: 'Tell us more' }))),
      requestDeveloperStatus: vi.fn()
        .mockResolvedValueOnce(ok(developer({ status: 'Approved', isActive: true, acceptedTermsVersion: 2 })))
        .mockResolvedValueOnce(failed(409))
        .mockResolvedValueOnce(failed(403))
        .mockResolvedValueOnce(failed(500)),
    };
    const { fixture, component } = await render(portal);
    expect(html(fixture).querySelector('.spark-developer-note')!.textContent).toContain('Tell us more');

    component.accepted.set(true);
    await component.request();
    expect(component.notice()).toBe('dev.status.Approved|{0}');

    component.status.set(developer({ status: 'Rejected' }));
    component.accepted.set(true);
    await component.request();
    expect(portal.developerStatus).toHaveBeenCalledTimes(2);
    expect(component.error()).toBe('dev.termsChanged|{0}');

    component.accepted.set(true);
    await component.request();
    expect(component.error()).toBe('dev.status.Revoked|{0}');

    component.accepted.set(true);
    await component.request();
    expect(component.error()).toBe('dev.requestFailed|{0}');
    expect(component.busy()).toBe(false);
  });

  it('an active developer whose terms were raised is asked to accept again, and gets a registration token', async () => {
    const portal = {
      developerStatus: vi.fn().mockResolvedValue(ok(developer({
        status: 'Approved', isActive: true, acceptedTermsVersion: 1, requestedAt: '2026-10-01T10:00:00Z', decidedAt: '2026-10-02T10:00:00Z',
      }))),
      issueRegistrationToken: vi.fn()
        .mockResolvedValueOnce(ok({ initialAccessToken: 'iat-secret', expiresIn: 600 }))
        .mockResolvedValueOnce(ok({ initialAccessToken: 'short', expiresIn: 5 }))
        .mockResolvedValueOnce(failed(500)),
    };
    const { fixture, component, secrets } = await render(portal);

    expect(html(fixture).querySelector('.spark-developer-terms-changed')).not.toBeNull();
    expect(button(fixture, '.spark-developer-request')!.textContent).toContain('dev.acceptAgain');
    expect(html(fixture).querySelector('.spark-developer-applications')).not.toBeNull();

    button(fixture, '.spark-developer-token')!.click();
    await settle(fixture);
    expect(secrets.show).toHaveBeenCalledWith({ title: 'dev.tokenTitle|{0}', message: 'dev.tokenMessage|10', value: 'iat-secret' });

    await component.issueToken();
    expect(secrets.show).toHaveBeenLastCalledWith(expect.objectContaining({ message: 'dev.tokenMessage|1', value: 'short' }));

    await component.issueToken();
    expect(component.error()).toBe('dev.tokenFailed|{0}');
    expect(component.busy()).toBe(false);
  });

  it('a status that cannot be loaded is an error, and a revoked developer cannot ask again', async () => {
    const { component } = await render({ developerStatus: vi.fn().mockResolvedValue(failed(500)) });
    expect(component.error()).toBe('loadFailed|{0}');
    expect(component.loading()).toBe(false);

    TestBed.resetTestingModule();
    const revoked = await render({ developerStatus: vi.fn().mockResolvedValue(ok(developer({ status: 'Revoked', acceptedTermsVersion: 2 }))) });
    expect(html(revoked.fixture).querySelector('.spark-developer-request')).toBeNull();
    expect(html(revoked.fixture).querySelector('.spark-developer-token')).toBeNull();
  });
});
