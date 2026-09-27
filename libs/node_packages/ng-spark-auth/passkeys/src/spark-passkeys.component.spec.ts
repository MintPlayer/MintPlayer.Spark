import { ComponentFixture, TestBed } from '@angular/core/testing';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkPasskeysComponent } from './spark-passkeys.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { SparkPasskey } from '@mintplayer/ng-spark-auth/models';

/**
 * jsdom implements none of WebAuthn. The component reads `passkeysSupported()` once, at
 * construction, so the stub must be in place before the component is created.
 */
function installWebAuthn() {
  (globalThis as Record<string, unknown>)['PublicKeyCredential'] = Object.assign(function () { }, {
    parseCreationOptionsFromJSON: vi.fn(),
    parseRequestOptionsFromJSON: vi.fn(),
  });
  Object.defineProperty(globalThis.navigator, 'credentials', {
    value: { create: vi.fn(), get: vi.fn() },
    configurable: true,
  });
  Object.defineProperty(globalThis.window, 'isSecureContext', { value: true, configurable: true });
}

function removeWebAuthn() {
  delete (globalThis as Record<string, unknown>)['PublicKeyCredential'];
  Object.defineProperty(globalThis.navigator, 'credentials', { value: undefined, configurable: true });
  Object.defineProperty(globalThis.window, 'isSecureContext', { value: false, configurable: true });
}

function passkey(overrides: Partial<SparkPasskey> = {}): SparkPasskey {
  return {
    id: 'cred-1', name: 'Laptop', createdAt: '2026-01-02', isBackedUp: false,
    isBackupEligible: false, transports: ['internal'], ...overrides,
  };
}

/** Resolves only when the test says so, to observe the in-flight (busy / loading) state. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

function configure(authOverrides: Record<string, unknown> = {}, supported = true) {
  if (supported) installWebAuthn(); else removeWebAuthn();

  const auth: any = {
    passkeys: vi.fn().mockResolvedValue([]),
    registerPasskey: vi.fn().mockResolvedValue({ success: true }),
    renamePasskey: vi.fn().mockResolvedValue({ success: true }),
    removePasskey: vi.fn().mockResolvedValue({ success: true }),
    ...authOverrides,
  };

  TestBed.configureTestingModule({
    imports: [SparkPasskeysComponent],
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
    ],
  });

  const fixture = TestBed.createComponent(SparkPasskeysComponent);
  return { fixture, component: fixture.componentInstance, auth };
}

async function render(fixture: ComponentFixture<unknown>) {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
}

const el = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
const text = (fixture: ComponentFixture<unknown>) => el(fixture).textContent ?? '';
const buttons = (fixture: ComponentFixture<unknown>) =>
  Array.from(el(fixture).querySelectorAll('button')) as HTMLButtonElement[];
const removeButtons = (fixture: ComponentFixture<unknown>) =>
  buttons(fixture).filter((b) => b.textContent!.includes('auth.passkeyRemove'));
const addButton = (fixture: ComponentFixture<unknown>) =>
  buttons(fixture).find((b) => b.textContent!.includes('auth.passkeyAdd'))!;
const dangerAlert = (fixture: ComponentFixture<unknown>) =>
  Array.from(el(fixture).querySelectorAll('bs-alert'))
    .map((a) => a.textContent!.trim())
    .filter((t) => t.startsWith('auth.passkey') && t !== 'auth.passkeysNone');

describe('SparkPasskeysComponent', () => {
  afterEach(() => removeWebAuthn());

  describe('listing', () => {
    it('shows a spinner and no buttons while the list is loading', async () => {
      const pending = deferred<SparkPasskey[]>();
      const { fixture, component } = configure({ passkeys: vi.fn(() => pending.promise) });
      fixture.detectChanges();

      expect(component.loading()).toBe(true);
      expect(el(fixture).querySelector('bs-spinner')).not.toBeNull();
      expect(buttons(fixture)).toHaveLength(0);

      pending.resolve([]);
      await render(fixture);
      expect(component.loading()).toBe(false);
      expect(el(fixture).querySelector('bs-spinner')).toBeNull();
    });

    it('renders each passkey with its label, falls back for an unnamed one, and marks synced ones', async () => {
      const { fixture } = configure({
        passkeys: vi.fn().mockResolvedValue([
          passkey({ id: 'a', name: 'Laptop', isBackedUp: true }),
          passkey({ id: 'b', name: null, createdAt: '2026-03-04' }),
        ]),
      });
      await render(fixture);

      const rows = Array.from(el(fixture).querySelectorAll('.border-bottom')).map((r) => r.textContent!);
      expect(rows).toHaveLength(2);
      expect(rows[0]).toContain('Laptop');
      expect(rows[0]).toContain('auth.passkeySynced');
      // A null name must not render as an empty row nor as the literal "null".
      expect(rows[1]).toContain('auth.passkeyUnnamed');
      expect(rows[1]).toContain('2026-03-04');
      expect(rows[1]).not.toContain('auth.passkeySynced');
      expect(removeButtons(fixture)).toHaveLength(2);
      expect(addButton(fixture)).toBeDefined();
      expect(text(fixture)).not.toContain('auth.passkeysNone');
    });

    it('says there are no passkeys when the list is empty, and still offers enrollment', async () => {
      const { fixture } = configure();
      await render(fixture);

      expect(text(fixture)).toContain('auth.passkeysNone');
      expect(removeButtons(fixture)).toHaveLength(0);
      expect(addButton(fixture).disabled).toBe(false);
    });

    it('raises the list-failed banner when the list cannot be loaded, instead of claiming there are none', async () => {
      const { fixture, component } = configure({ passkeys: vi.fn().mockRejectedValue(new Error('500')) });
      await render(fixture);

      expect(component.errorKey()).toBe('auth.passkeyListFailed');
      expect(component.loading()).toBe(false);
      expect(dangerAlert(fixture)).toContain('auth.passkeyListFailed');
    });

    it('offers no enrollment at all when the browser cannot run the ceremony', async () => {
      // Offering a button the browser cannot act on would fail at the moment of the click.
      const { fixture, component } = configure({}, false);
      await render(fixture);

      expect(component.supported()).toBe(false);
      expect(text(fixture)).toContain('auth.passkeyUnsupported');
      expect(buttons(fixture)).toHaveLength(0);
      expect(text(fixture)).not.toContain('auth.passkeysDescription');
    });
  });

  describe('enrolling', () => {
    it('registers from the add button and reloads the list with the new passkey', async () => {
      const passkeys = vi.fn()
        .mockResolvedValueOnce([])
        .mockResolvedValueOnce([passkey({ id: 'new', name: 'Phone' })]);
      const { fixture, auth } = configure({ passkeys });
      await render(fixture);

      addButton(fixture).click();
      await render(fixture);

      expect(auth.registerPasskey).toHaveBeenCalledTimes(1);
      expect(passkeys).toHaveBeenCalledTimes(2);
      expect(text(fixture)).toContain('Phone');
      expect(text(fixture)).not.toContain('auth.passkeysNone');
      expect(dangerAlert(fixture)).toEqual([]);
    });

    it('disables every action while a ceremony is in flight, and re-enables them afterwards', async () => {
      // Two overlapping ceremonies would race on the same authenticator.
      const pending = deferred<{ success: boolean }>();
      const { fixture, component } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey()]),
        registerPasskey: vi.fn(() => pending.promise),
      });
      await render(fixture);

      const registering = component.register();
      await render(fixture);
      expect(component.busy()).toBe(true);
      expect(buttons(fixture).every((b) => b.disabled)).toBe(true);

      pending.resolve({ success: false, error: 'failed' } as any);
      await registering;
      await render(fixture);
      expect(component.busy()).toBe(false);
      expect(buttons(fixture).some((b) => b.disabled)).toBe(false);
    });

    it('treats a dismissed prompt as "not now": no banner and no reload', async () => {
      const { fixture, component, auth } = configure({
        registerPasskey: vi.fn().mockResolvedValue({ success: false, error: 'cancelled' }),
      });
      await render(fixture);

      await component.register();
      await render(fixture);

      expect(component.errorKey()).toBe('');
      expect(dangerAlert(fixture)).toEqual([]);
      expect(auth.passkeys).toHaveBeenCalledTimes(1);
      expect(component.busy()).toBe(false);
    });

    it.each([
      ['unsupported', 'auth.passkeyUnsupported'],
      ['no_credential', 'auth.passkeyNoCredential'],
      ['failed', 'auth.passkeyFailed'],
      ['locked_out', 'auth.passkeyFailed'],
      [undefined, 'auth.passkeyFailed'],
    ])('maps a %s enrollment failure to %s and does not reload', async (error, key) => {
      const { fixture, component, auth } = configure({
        registerPasskey: vi.fn().mockResolvedValue({ success: false, error }),
      });
      await render(fixture);

      await component.register();
      await render(fixture);

      expect(component.errorKey()).toBe(key);
      expect(dangerAlert(fixture)).toContain(key);
      expect(auth.passkeys).toHaveBeenCalledTimes(1);
    });

    it('clears a previous error when a new action starts', async () => {
      const { fixture, component } = configure({ passkeys: vi.fn().mockRejectedValueOnce(new Error('x')).mockResolvedValue([]) });
      await render(fixture);
      expect(component.errorKey()).toBe('auth.passkeyListFailed');

      await component.register();
      await render(fixture);

      expect(component.errorKey()).toBe('');
      expect(dangerAlert(fixture)).toEqual([]);
    });

    it('releases the busy state even when the service throws', async () => {
      // try/finally without catch: the error propagates, but the page must not stay locked.
      const { component } = configure({ registerPasskey: vi.fn().mockRejectedValue(new Error('boom')) });
      await flush();

      await expect(component.register()).rejects.toThrow('boom');
      expect(component.busy()).toBe(false);
    });
  });

  describe('removing', () => {
    it('removes the passkey whose button was clicked and reloads the list', async () => {
      const passkeys = vi.fn()
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Laptop' }), passkey({ id: 'b', name: 'Phone' })])
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Laptop' })]);
      const { fixture, auth } = configure({ passkeys });
      await render(fixture);

      removeButtons(fixture)[1].click();
      await render(fixture);

      expect(auth.removePasskey).toHaveBeenCalledWith('b');
      expect(passkeys).toHaveBeenCalledTimes(2);
      expect(text(fixture)).toContain('Laptop');
      expect(text(fixture)).not.toContain('Phone');
    });

    it('explains a refused last-credential removal with its own message, not the generic failure', async () => {
      // The removal did not fail — it was refused to stop the owner locking themselves out.
      const { fixture, component, auth } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey()]),
        removePasskey: vi.fn().mockResolvedValue({ success: false, error: 'last_credential' }),
      });
      await render(fixture);

      await component.remove(component.passkeys()[0]);
      await render(fixture);

      expect(component.errorKey()).toBe('auth.passkeyLastCredential');
      expect(dangerAlert(fixture)).toContain('auth.passkeyLastCredential');
      expect(auth.passkeys).toHaveBeenCalledTimes(1);
      // The passkey is still there, because it was not removed.
      expect(removeButtons(fixture)).toHaveLength(1);
      expect(component.busy()).toBe(false);
    });
  });

  describe('renaming', () => {
    it('renames by id and reloads the list', async () => {
      const passkeys = vi.fn()
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Laptop' })])
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Work laptop' })]);
      const { fixture, component, auth } = configure({ passkeys });
      await render(fixture);

      await component.rename(component.passkeys()[0], 'Work laptop');
      await render(fixture);

      expect(auth.renamePasskey).toHaveBeenCalledWith('a', 'Work laptop');
      expect(passkeys).toHaveBeenCalledTimes(2);
      expect(text(fixture)).toContain('Work laptop');
      expect(component.busy()).toBe(false);
    });

    it('shows the mapped error and keeps the old name when renaming fails', async () => {
      const { fixture, component, auth } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey({ id: 'a', name: 'Laptop' })]),
        renamePasskey: vi.fn().mockResolvedValue({ success: false, error: 'failed' }),
      });
      await render(fixture);

      await component.rename(component.passkeys()[0], 'Other');
      await render(fixture);

      expect(component.errorKey()).toBe('auth.passkeyFailed');
      expect(dangerAlert(fixture)).toContain('auth.passkeyFailed');
      expect(auth.passkeys).toHaveBeenCalledTimes(1);
      expect(text(fixture)).toContain('Laptop');
      expect(component.busy()).toBe(false);
    });

    it('edits one row inline from its rename button, prefilled with the current name', async () => {
      const { fixture } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey({ id: 'a', name: 'Laptop' }), passkey({ id: 'b', name: 'Phone' })]),
      });
      await render(fixture);

      renameButtons(fixture)[1].click();
      await render(fixture);

      const inputs = nameInputs(fixture);
      expect(inputs.length).toBe(1);
      expect(inputs[0].value).toBe('Phone');
      // The other row keeps its actions; the edited row trades them for save / cancel.
      expect(renameButtons(fixture).length).toBe(1);
      expect(removeButtons(fixture).length).toBe(1);
      expect(buttonWith(fixture, 'auth.passkeySave')).toBeTruthy();
    });

    it('saves the typed name by id from the form, then leaves edit mode', async () => {
      const passkeys = vi.fn()
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Laptop' })])
        .mockResolvedValueOnce([passkey({ id: 'a', name: 'Work laptop' })]);
      const { fixture, auth } = configure({ passkeys });
      await render(fixture);

      renameButtons(fixture)[0].click();
      await render(fixture);
      const input = nameInputs(fixture)[0];
      input.value = 'Work laptop';
      input.dispatchEvent(new Event('input'));
      buttonWith(fixture, 'auth.passkeySave')!.click();
      await render(fixture);

      expect(auth.renamePasskey).toHaveBeenCalledWith('a', 'Work laptop');
      expect(nameInputs(fixture).length).toBe(0);
      expect(text(fixture)).toContain('Work laptop');
    });

    it('stays in edit mode with the typed name when saving fails', async () => {
      const { fixture, component } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey({ id: 'a', name: 'Laptop' })]),
        renamePasskey: vi.fn().mockResolvedValue({ success: false, error: 'failed' }),
      });
      await render(fixture);

      renameButtons(fixture)[0].click();
      await render(fixture);
      const input = nameInputs(fixture)[0];
      input.value = 'Other';
      input.dispatchEvent(new Event('input'));
      buttonWith(fixture, 'auth.passkeySave')!.click();
      await render(fixture);

      expect(component.editingId()).toBe('a');
      expect(nameInputs(fixture)[0].value).toBe('Other');
      expect(dangerAlert(fixture)).toContain('auth.passkeyFailed');
    });

    it('cancels without calling the server, from the cancel button and from Escape', async () => {
      const { fixture, component, auth } = configure({
        passkeys: vi.fn().mockResolvedValue([passkey({ id: 'a', name: 'Laptop' })]),
      });
      await render(fixture);

      renameButtons(fixture)[0].click();
      await render(fixture);
      buttonWith(fixture, 'auth.passkeyCancel')!.click();
      await render(fixture);
      expect(component.editingId()).toBeNull();

      renameButtons(fixture)[0].click();
      await render(fixture);
      nameInputs(fixture)[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      await render(fixture);

      expect(component.editingId()).toBeNull();
      expect(nameInputs(fixture).length).toBe(0);
      expect(auth.renamePasskey).not.toHaveBeenCalled();
      expect(text(fixture)).toContain('Laptop');
    });
  });
});

const buttonWith = (fixture: ComponentFixture<unknown>, key: string) =>
  buttons(fixture).find((b) => b.textContent!.includes(key));
const renameButtons = (fixture: ComponentFixture<unknown>) =>
  buttons(fixture).filter((b) => b.textContent!.includes('auth.passkeyRename'));
const nameInputs = (fixture: ComponentFixture<unknown>) =>
  Array.from(el(fixture).querySelectorAll('input[formcontrolname="name"]')) as HTMLInputElement[];
