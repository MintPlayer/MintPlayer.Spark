import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import {
  SPARK_ACCOUNT_OVERVIEW_LINKS,
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  defaultSparkAuthConfig,
  provideSparkAccountProfileFields,
} from '@mintplayer/ng-spark/auth/models';
import { SparkConfirmEmailComponent } from '@mintplayer/ng-spark/auth/confirm-email';
import { accountMessages } from './account-messages';
import { SparkAccountOverviewComponent } from './spark-account-overview.component';
import { SparkAccountProfileComponent } from './spark-account-profile.component';
import { SparkChangePasswordComponent } from './spark-change-password.component';
import { SparkTwoFactorSetupComponent } from './spark-two-factor-setup.component';
import { SparkExternalLoginsComponent } from './spark-external-logins.component';
import { SparkPersonalDataComponent } from './spark-personal-data.component';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

function configure(auth: Record<string, unknown>, extra: unknown[] = [], query: Record<string, string> = {}) {
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkAuthService, useValue: { isAuthenticated: () => false, user: () => null, externalLoginPending: () => false, takeExternalLoginResult: () => null, ...auth } },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: { login: '/login', profile: '/account/profile', personalData: '/account/personal-data' } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ...(extra as any[]),
    ],
  });
}

async function render<T>(fixture: ComponentFixture<T>): Promise<ComponentFixture<T>> {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
  return fixture;
}

const text = (fixture: ComponentFixture<unknown>) => (fixture.nativeElement as HTMLElement).textContent ?? '';

describe('accountMessages', () => {
  it('prefers server messages, then an error-code key, then a status key', () => {
    expect(accountMessages({ success: false, errors: { A: ['one', 'two'], B: ['three'] } })).toEqual(['one', 'two', 'three']);
    expect(accountMessages({ success: false, error: 'reauthentication_required' })).toEqual(['auth.accountError.reauthentication_required']);
    expect(accountMessages({ success: false, status: 404 })).toEqual(['auth.accountUnavailable']);
    expect(accountMessages({ success: false })).toEqual(['auth.accountFailed']);
    expect(accountMessages({ success: true })).toEqual([]);
  });
});

describe('SparkConfirmEmailComponent', () => {
  it('confirms from the link query', async () => {
    const confirmEmail = vi.fn().mockResolvedValue({ success: true });
    configure({ confirmEmail }, [], { userId: 'users/1', code: 'abc' });
    const fixture = await render(TestBed.createComponent(SparkConfirmEmailComponent));
    expect(confirmEmail).toHaveBeenCalledWith('users/1', 'abc', null);
    expect(text(fixture)).toContain('auth.emailConfirmed');
  });

  it('reports an email change', async () => {
    const confirmEmail = vi.fn().mockResolvedValue({ success: true });
    configure({ confirmEmail }, [], { userId: 'users/1', code: 'abc', changedEmail: 'new@example.test' });
    const fixture = await render(TestBed.createComponent(SparkConfirmEmailComponent));
    expect(confirmEmail).toHaveBeenCalledWith('users/1', 'abc', 'new@example.test');
    expect(text(fixture)).toContain('auth.emailChanged');
  });

  it('refuses a link without its parameters, without calling the server', async () => {
    const confirmEmail = vi.fn();
    configure({ confirmEmail }, [], { userId: 'users/1' });
    const fixture = await render(TestBed.createComponent(SparkConfirmEmailComponent));
    expect(confirmEmail).not.toHaveBeenCalled();
    expect(text(fixture)).toContain('auth.invalidConfirmLink');
  });

  it('shows an invalid link when the server refuses', async () => {
    configure({ confirmEmail: vi.fn().mockResolvedValue({ success: false, status: 400 }) }, [], { userId: 'u', code: 'c' });
    const fixture = await render(TestBed.createComponent(SparkConfirmEmailComponent));
    expect(text(fixture)).toContain('auth.invalidConfirmLink');
  });
});

describe('SparkChangePasswordComponent', () => {
  it('refuses mismatching passwords locally', async () => {
    const setPassword = vi.fn();
    configure({ setPassword });
    const fixture = await render(TestBed.createComponent(SparkChangePasswordComponent));
    fixture.componentInstance.form.setValue({ currentPassword: '', newPassword: 'a', confirmPassword: 'b' });
    await fixture.componentInstance.submit();
    expect(setPassword).not.toHaveBeenCalled();
    expect(fixture.componentInstance.messages()).toEqual(['auth.passwordMismatch']);
  });

  it('sets a first password without a current one, and shows the server\'s refusal', async () => {
    const setPassword = vi.fn()
      .mockResolvedValueOnce({ success: false, status: 400, errors: { CurrentPasswordRequired: ['The current password is required to change it.'] } })
      .mockResolvedValueOnce({ success: true });
    configure({ setPassword });
    const fixture = await render(TestBed.createComponent(SparkChangePasswordComponent));
    const c = fixture.componentInstance;

    c.form.setValue({ currentPassword: '', newPassword: 'N3w!', confirmPassword: 'N3w!' });
    await c.submit();
    expect(setPassword).toHaveBeenLastCalledWith('N3w!', null);
    expect(c.messages()).toEqual(['The current password is required to change it.']);

    c.form.setValue({ currentPassword: 'old', newPassword: 'N3w!', confirmPassword: 'N3w!' });
    await c.submit();
    expect(setPassword).toHaveBeenLastCalledWith('N3w!', 'old');
    expect(c.saved()).toBe(true);
  });
});

describe('SparkAccountProfileComponent', () => {
  const profile = { userName: 'jane', email: 'jane@example.test', fields: { Bio: 'hello', Newsletter: true }, preferredCulture: 'nl-BE' };

  function setup(overrides: Record<string, unknown> = {}) {
    const auth = {
      profile: vi.fn().mockResolvedValue({ success: true, value: profile }),
      accountInfo: vi.fn().mockResolvedValue({ success: true, value: { email: 'jane@example.test', isEmailConfirmed: false } }),
      updateProfile: vi.fn().mockResolvedValue({ success: true, value: profile }),
      changeEmail: vi.fn().mockResolvedValue({ success: true, value: { email: 'jane@example.test', isEmailConfirmed: true } }),
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [], emailChange: true }),
      ...overrides,
    };
    configure(auth, [
      provideSparkAccountProfileFields(
        { name: 'Newsletter', label: 'Newsletter', type: 'checkbox', order: 2 },
        { name: 'Bio', label: 'Bio', type: 'textarea', order: 1 },
      ),
    ]);
    const fixture = TestBed.createComponent(SparkAccountProfileComponent);
    const http = TestBed.inject(HttpTestingController);
    return { fixture, auth, http };
  }

  it('loads the profile, the app fields in order, and keeps a stored culture not in the language list', async () => {
    const { fixture, http } = setup();
    await flush();
    http.expectOne('/spark/culture').flush({ languages: { en: { en: 'English' }, nl: { en: 'Dutch' } }, defaultLanguage: 'en' });
    await render(fixture);
    const c = fixture.componentInstance;

    expect(c.form.controls.userName.value).toBe('jane');
    expect(c.form.controls.preferredCulture.value).toBe('nl-BE');
    expect(c.cultureOptions().map(o => o.value)).toEqual(['en', 'nl', 'nl-BE']);
    expect(c.form.controls.fields.value).toEqual({ Bio: 'hello', Newsletter: true });
    const labels = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.spark-profile-field')].map(e => e.textContent?.trim());
    expect(labels[0]).toContain('Bio');
    expect(labels[1]).toContain('Newsletter');
    expect(text(fixture)).toContain('auth.emailNotConfirmed');
  });

  it('saves user name, culture (empty = clear) and fields', async () => {
    const { fixture, auth, http } = setup();
    await flush();
    http.expectOne('/spark/culture').flush({ languages: {}, defaultLanguage: 'en' });
    await render(fixture);
    const c = fixture.componentInstance;

    c.form.controls.preferredCulture.setValue('');
    await c.save();
    expect(auth.updateProfile).toHaveBeenCalledWith({
      userName: 'jane',
      preferredCulture: null,
      fields: { Bio: 'hello', Newsletter: true },
    });
    expect(c.saved()).toBe(true);
  });

  it('shows field errors next to their control and the rest in the banner', async () => {
    const { fixture, http } = setup({
      updateProfile: vi.fn().mockResolvedValue({
        success: false, status: 400,
        errors: { PreferredCulture: ['Not a known culture name.'], DuplicateUserName: ['Taken.'], Other: ['General.'] },
      }),
    });
    await flush();
    http.expectOne('/spark/culture').flush({ languages: {}, defaultLanguage: 'en' });
    await render(fixture);
    await fixture.componentInstance.save();
    fixture.detectChanges();

    expect(fixture.componentInstance.messages()).toEqual(['General.']);
    expect(text(fixture)).toContain('Not a known culture name.');
    expect(text(fixture)).toContain('Taken.');
  });

  it('requests an email change and says to check the new inbox', async () => {
    const { fixture, auth, http } = setup();
    await flush();
    http.expectOne('/spark/culture').flush({ languages: {}, defaultLanguage: 'en' });
    await render(fixture);
    fixture.componentInstance.emailForm.setValue({ newEmail: 'new@example.test' });
    await fixture.componentInstance.changeEmail();
    fixture.detectChanges();
    expect(auth.changeEmail).toHaveBeenCalledWith('new@example.test');
    expect(text(fixture)).toContain('auth.emailChangeSent');
  });

  it('puts the new-email input and its button in one input group when the server allows a change', async () => {
    const { fixture, http } = setup();
    await flush();
    http.expectOne('/spark/culture').flush({ languages: {}, defaultLanguage: 'en' });
    await render(fixture);
    const group = (fixture.nativeElement as HTMLElement).querySelector('bs-input-group');
    expect(group?.querySelector('input#newEmail')).not.toBeNull();
    expect(group?.querySelector('button[type=submit]')?.textContent).toContain('auth.changeEmail');
  });

  for (const [name, capabilities] of [
    ['the server does not opt in', vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [] })],
    ['the capabilities call fails', vi.fn().mockRejectedValue(new Error('offline'))],
  ] as const) {
    it(`shows the email but offers no change when ${name}`, async () => {
      const { fixture, http } = setup({ capabilities });
      await flush();
      http.expectOne('/spark/culture').flush({ languages: {}, defaultLanguage: 'en' });
      await render(fixture);
      expect(text(fixture)).toContain('jane@example.test');
      expect((fixture.nativeElement as HTMLElement).querySelector('#newEmail')).toBeNull();
    });
  }
});

describe('SparkTwoFactorSetupComponent', () => {
  const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 53 53"><rect width="53" height="53" fill="#fff"/></svg>';

  it('shows the server QR as an image, never as markup, and enables with the code', async () => {
    const twoFactor = vi.fn()
      .mockResolvedValueOnce({ success: true, value: { sharedKey: 'ABCDEFGH', recoveryCodesLeft: 0, isTwoFactorEnabled: false, isMachineRemembered: false } })
      .mockResolvedValueOnce({ success: true, value: { sharedKey: 'ABCDEFGH', recoveryCodesLeft: 10, recoveryCodes: ['r1', 'r2'], isTwoFactorEnabled: true, isMachineRemembered: false } });
    const authenticatorUri = vi.fn().mockResolvedValue({ success: true, value: { sharedKey: 'ABCDEFGH', authenticatorUri: 'otpauth://x', qrCodeSvg: svg } });
    configure({ twoFactor, authenticatorUri });
    const fixture = await render(TestBed.createComponent(SparkTwoFactorSetupComponent));
    const el = fixture.nativeElement as HTMLElement;

    const img = el.querySelector('img.spark-2fa-qr') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg));
    expect(el.querySelector('svg')).toBeNull();
    expect(el.querySelector('.spark-2fa-key')?.textContent).toContain('ABCD EFGH');

    fixture.componentInstance.form.setValue({ code: '123 456' });
    await fixture.componentInstance.enable();
    fixture.detectChanges();
    expect(twoFactor).toHaveBeenLastCalledWith({ enable: true, twoFactorCode: '123456' });
    expect(el.querySelector('.spark-recovery-codes')?.textContent).toContain('r1');
    expect(el.querySelector('.spark-2fa-enabled')).not.toBeNull();
  });

  it('regenerates recovery codes and disables after confirmation', async () => {
    const enabled = { sharedKey: 'K', recoveryCodesLeft: 3, isTwoFactorEnabled: true, isMachineRemembered: true };
    const twoFactor = vi.fn().mockResolvedValue({ success: true, value: enabled });
    configure({ twoFactor, authenticatorUri: vi.fn() });
    const fixture = await render(TestBed.createComponent(SparkTwoFactorSetupComponent));
    const confirmSpy = vi.spyOn(globalThis, 'confirm').mockReturnValue(true);

    await fixture.componentInstance.regenerateRecoveryCodes();
    expect(twoFactor).toHaveBeenLastCalledWith({ resetRecoveryCodes: true });
    await fixture.componentInstance.disable();
    expect(twoFactor).toHaveBeenLastCalledWith({ enable: false });
    await fixture.componentInstance.forgetMachine();
    expect(twoFactor).toHaveBeenLastCalledWith({ forgetMachine: true });
    confirmSpy.mockRestore();
  });

  it('offers the external-login bypass only when the server does and 2FA is on, and asks a code to switch it on', async () => {
    const enabled = { sharedKey: null, recoveryCodesLeft: 10, isTwoFactorEnabled: true, isMachineRemembered: false };
    const setExternalLoginTwoFactor = vi.fn().mockResolvedValue({ success: true, value: { bypass: true, twoFactorEnabled: true } });
    configure({
      twoFactor: vi.fn().mockResolvedValue({ success: true, value: enabled }),
      authenticatorUri: vi.fn(),
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [], externalLoginTwoFactorBypass: true }),
      externalLoginTwoFactor: vi.fn().mockResolvedValue({ success: true, value: { bypass: false, twoFactorEnabled: true } }),
      setExternalLoginTwoFactor,
    });
    const fixture = await render(TestBed.createComponent(SparkTwoFactorSetupComponent));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.spark-2fa-external-bypass')).not.toBeNull();

    await fixture.componentInstance.toggleBypass(true);
    fixture.detectChanges();
    expect(setExternalLoginTwoFactor).not.toHaveBeenCalled();
    expect(el.querySelector('#bypassCode')).not.toBeNull();

    fixture.componentInstance.bypassForm.setValue({ code: '123 456' });
    await fixture.componentInstance.confirmBypass();
    expect(setExternalLoginTwoFactor).toHaveBeenLastCalledWith(true, '123456');
    expect(fixture.componentInstance.bypass()).toBe(true);

    setExternalLoginTwoFactor.mockResolvedValue({ success: true, value: { bypass: false, twoFactorEnabled: true } });
    await fixture.componentInstance.toggleBypass(false);
    expect(setExternalLoginTwoFactor).toHaveBeenLastCalledWith(false, undefined);
  });

  it('hides the external-login bypass when the server does not offer it', async () => {
    const enabled = { sharedKey: null, recoveryCodesLeft: 10, isTwoFactorEnabled: true, isMachineRemembered: false };
    configure({
      twoFactor: vi.fn().mockResolvedValue({ success: true, value: enabled }),
      authenticatorUri: vi.fn(),
      capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Full', externalProviders: [] }),
      externalLoginTwoFactor: vi.fn(),
    });
    const fixture = await render(TestBed.createComponent(SparkTwoFactorSetupComponent));
    expect((fixture.nativeElement as HTMLElement).querySelector('.spark-2fa-external-bypass')).toBeNull();
  });

  it('hides "Forget this browser" after forgetting, though the server still answers remembered', async () => {
    // MapIdentityApi reads isMachineRemembered from the cookie of the very request that deletes it.
    const remembered = { sharedKey: 'K', recoveryCodesLeft: 3, isTwoFactorEnabled: true, isMachineRemembered: true };
    const twoFactor = vi.fn().mockResolvedValue({ success: true, value: remembered });
    configure({ twoFactor, authenticatorUri: vi.fn() });
    const fixture = await render(TestBed.createComponent(SparkTwoFactorSetupComponent));
    expect(fixture.componentInstance.state()?.isMachineRemembered).toBe(true);

    await fixture.componentInstance.forgetMachine();
    expect(fixture.componentInstance.state()?.isMachineRemembered).toBe(false);
  });
});

describe('SparkExternalLoginsComponent', () => {
  it('lists linked and available logins; the last credential cannot be removed', async () => {
    const externalLogins = vi.fn().mockResolvedValue({
      linked: [{ provider: 'GitHub', providerKey: '1', displayName: 'GitHub', canUnlink: false }],
      available: [{ provider: 'Google', displayName: 'Google' }],
    });
    const unlinkProvider = vi.fn().mockResolvedValue({ success: false, error: 'last_credential' });
    configure({ externalLogins, unlinkProvider, linkProvider: vi.fn().mockResolvedValue({ success: true }) });
    const fixture = await render(TestBed.createComponent(SparkExternalLoginsComponent));
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.spark-linked-login button')?.hasAttribute('disabled')).toBe(true);
    expect(el.querySelector('.spark-available-login')?.textContent).toContain('Google');

    await fixture.componentInstance.unlink('GitHub', '1');
    expect(fixture.componentInstance.errorKey()).toBe('auth.unlinkError.last_credential');
  });

  it('says so when linking is not enabled (404)', async () => {
    configure({ externalLogins: vi.fn().mockRejectedValue({ status: 404 }) });
    const fixture = await render(TestBed.createComponent(SparkExternalLoginsComponent));
    expect(text(fixture)).toContain('auth.externalLoginsUnavailable');
  });

  const someLogins = () => vi.fn().mockResolvedValue({
    linked: [{ provider: 'GitHub', providerKey: '1', displayName: 'GitHub', canUnlink: true }],
    available: [{ provider: 'Google', displayName: 'Google' }],
  });

  it('shows the ?sparkExternalLogin a redirect-mode link came back with', async () => {
    const takeExternalLoginResult = vi.fn(() => 'login_already_associated');
    configure({ externalLogins: someLogins(), takeExternalLoginResult });
    const fixture = await render(TestBed.createComponent(SparkExternalLoginsComponent));

    expect(takeExternalLoginResult).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.errorKey()).toBe('auth.externalLoginError.login_already_associated');
    expect(text(fixture)).toContain('auth.externalLoginError.login_already_associated');
  });

  it("shows a failed link's translated error, and nothing for popup_closed", async () => {
    const linkProvider = vi.fn().mockResolvedValueOnce({ success: false, error: 'remote_failure' })
      .mockResolvedValueOnce({ success: false, error: 'popup_closed' });
    configure({ externalLogins: someLogins(), linkProvider });
    const fixture = await render(TestBed.createComponent(SparkExternalLoginsComponent));

    await fixture.componentInstance.link('Google');
    expect(fixture.componentInstance.errorKey()).toBe('auth.externalLoginError.remote_failure');

    await fixture.componentInstance.link('Google');
    expect(fixture.componentInstance.errorKey()).toBe('');
  });

  it('disables the link buttons and shows a spinner while a link popup is pending', async () => {
    const externalLoginPending = signal(false);
    configure({ externalLogins: someLogins(), externalLoginPending });
    const fixture = await render(TestBed.createComponent(SparkExternalLoginsComponent));
    const el = fixture.nativeElement as HTMLElement;

    externalLoginPending.set(true);
    fixture.detectChanges();
    expect(el.querySelector('.spark-available-login')?.hasAttribute('disabled')).toBe(true);
    expect(el.querySelector('.spark-link-pending bs-spinner')).not.toBeNull();

    externalLoginPending.set(false);
    fixture.detectChanges();
    expect(el.querySelector('.spark-available-login')?.hasAttribute('disabled')).toBe(false);
    expect(el.querySelector('.spark-link-pending')).toBeNull();
  });
});

describe('SparkPersonalDataComponent', () => {
  beforeEach(() => {
    (URL as any).createObjectURL = vi.fn().mockReturnValue('blob:x');
    (URL as any).revokeObjectURL = vi.fn();
  });
  afterEach(() => vi.restoreAllMocks());

  it('downloads the data as a JSON file', async () => {
    const personalData = vi.fn().mockResolvedValue({ success: true, value: { account: { id: 'users/1' } } });
    configure({ personalData });
    const fixture = await render(TestBed.createComponent(SparkPersonalDataComponent));
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    await fixture.componentInstance.download();
    expect(personalData).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
  });

  it('asks for the password only when the account has one, and otherwise explains the recent sign-in', async () => {
    configure({ personalData: vi.fn().mockResolvedValue({ success: true, value: { account: { hasPassword: false } } }) });
    const without = await render(TestBed.createComponent(SparkPersonalDataComponent));
    await without.whenStable();
    without.detectChanges();
    expect(without.nativeElement.querySelector('#deletePassword')).toBeNull();
    expect(without.nativeElement.querySelector('.spark-delete-recent-sign-in')?.textContent).toContain('auth.deleteRecentSignIn');

    TestBed.resetTestingModule();
    configure({ personalData: vi.fn().mockResolvedValue({ success: true, value: { account: { hasPassword: true } } }) });
    const withPassword = await render(TestBed.createComponent(SparkPersonalDataComponent));
    await withPassword.whenStable();
    withPassword.detectChanges();
    expect(withPassword.nativeElement.querySelector('#deletePassword')).not.toBeNull();
    expect(withPassword.nativeElement.querySelector('.spark-delete-recent-sign-in')).toBeNull();
  });

  it('deletes only after the checkbox, shows reauthentication_required, then navigates home on success', async () => {
    const deleteAccount = vi.fn()
      .mockResolvedValueOnce({ success: false, status: 403, error: 'reauthentication_required' })
      .mockResolvedValueOnce({ success: true });
    configure({ deleteAccount });
    const fixture = await render(TestBed.createComponent(SparkPersonalDataComponent));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const c = fixture.componentInstance;

    await c.deleteAccount();
    expect(deleteAccount).not.toHaveBeenCalled();

    c.form.setValue({ password: '', confirm: true });
    await c.deleteAccount();
    expect(deleteAccount).toHaveBeenLastCalledWith(null);
    expect(c.deleteMessages()).toEqual(['auth.accountError.reauthentication_required']);

    c.form.setValue({ password: 'pw', confirm: true });
    await c.deleteAccount();
    expect(deleteAccount).toHaveBeenLastCalledWith('pw');
    expect(navigate).toHaveBeenCalledWith('/');
  });
});

describe('SparkAccountOverviewComponent', () => {
  const allPages = {
    provide: SPARK_AUTH_ROUTE_PATHS,
    useValue: {
      profile: '/account/profile', changePassword: '/account/password', twoFactorSetup: '/account/two-factor',
      externalLogins: '/account/logins', passkeys: '/account/passkeys', personalData: '/account/personal-data',
    },
  };
  const links = (fixture: ComponentFixture<unknown>) =>
    [...(fixture.nativeElement as HTMLElement).querySelectorAll('a.spark-account-link')].map(a => a.getAttribute('href'));
  const capabilities = (localCredentials: string, passkeys?: boolean) =>
    vi.fn().mockResolvedValue({ localCredentials, externalProviders: [], passkeys });

  it('links only the mounted pages', async () => {
    configure({ capabilities: capabilities('Full') });
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/personal-data']);
  });

  // CodeCoverage's case: GitHub sign-in is on, ExternalLoginLinking is not, so the page is not served.
  it('hides connected logins unless the server serves them', async () => {
    configure({ capabilities: capabilities('Disabled') }, [allPages]);
    const hidden = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(hidden)).not.toContain('/account/logins');

    TestBed.resetTestingModule();
    configure({ capabilities: vi.fn().mockResolvedValue({ localCredentials: 'Disabled', externalProviders: [], externalLogins: true }) }, [allPages]);
    const shown = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(shown)).toContain('/account/logins');
  });

  it('offers the password and two-factor pages when the server signs in with passwords', async () => {
    configure({ capabilities: vi.fn().mockResolvedValue({ localCredentials: 'SignInOnly', externalProviders: [], twoFactor: true }) }, [allPages]);
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/password', '/account/two-factor', '/account/personal-data']);
  });

  it('hides the two-factor page unless the server serves it', async () => {
    configure({ capabilities: capabilities('SignInOnly') }, [allPages]);
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/password', '/account/personal-data']);
  });

  it('hides the password and two-factor pages under LocalCredentials = Disabled', async () => {
    configure({ capabilities: capabilities('Disabled', false) }, [allPages]);
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/personal-data']);
  });

  it('hides every server-switched page when the capabilities call fails', async () => {
    configure({ capabilities: vi.fn().mockRejectedValue(new Error('offline')) }, [allPages]);
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/personal-data']);
  });

  it('lists the links other route features contribute, after its own pages, in the current language', async () => {
    const extra = {
      provide: SPARK_ACCOUNT_OVERVIEW_LINKS,
      useValue: [{ key: 'connectedApplications', path: '/account/applications', label: { en: 'Connected applications', nl: 'Verbonden applicaties' } }],
    };
    configure({ capabilities: capabilities('Full') }, [extra]);
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(links(fixture)).toEqual(['/account/profile', '/account/personal-data', '/account/applications']);
    expect(text(fixture)).toContain('Connected applications');
  });

  // ⚠️ The passkeys *positive* case is not asserted: it also needs passkeysSupported(), which jsdom
  // cannot satisfy without faking the browser API it checks (same reason as the auth-bar spec).

  it('shows the user name, and the email only when it differs', async () => {
    const user = { isAuthenticated: true, userName: 'jane', email: 'jane@example.test', roles: [] };
    configure({ capabilities: capabilities('Full'), user: () => user });
    const fixture = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(text(fixture)).toContain('jane');
    expect(text(fixture)).toContain('jane@example.test');

    user.userName = user.email;
    const same = await render(TestBed.createComponent(SparkAccountOverviewComponent));
    expect(same.nativeElement.querySelector('.spark-account-email')).toBeNull();
  });
});
