import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- see the provider below
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkSignInComponent } from './spark-sign-in.component';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  SPARK_EXTERNAL_PROVIDERS,
  SparkAuthCapabilities,
  SparkExternalProviderPresentation,
  defaultSparkAuthConfig,
} from '@mintplayer/ng-spark-auth/models';

/**
 * #303 — customising a provider button, and #302's `returnUrl` carry-over.
 *
 * These need a **host component**. The existing sign-in specs route straight to
 * `SparkSignInComponent`, and the router instantiates a component with no projected content — so
 * neither `<ng-content>` nor a `TemplateRef` input is reachable that way. Projection is only usable
 * when the consumer hosts the component, which `withExternalLogin({ signIn: { path, component } })`
 * is for. Testing it any other way would pass while the feature stayed unreachable in practice.
 */
@Component({
  standalone: true,
  imports: [SparkSignInComponent],
  template: `
    <ng-template #button let-provider let-signIn="signIn">
      <button type="button" class="custom" (click)="signIn()">Continue with {{ provider.displayName }}</button>
    </ng-template>

    <spark-sign-in [providerTemplate]="projected() ? button : null" [returnUrl]="returnUrl()" />
  `,
})
class HostComponent {
  readonly projected = signal(true);
  readonly returnUrl = signal<string | undefined>(undefined);
}

const github = { scheme: 'GitHub', displayName: 'GitHub' };
const google = { scheme: 'Google', displayName: 'Google' };

const routes: Routes = [{ path: 'sign-in', component: HostComponent }];

function capabilities(overrides: Partial<SparkAuthCapabilities> = {}): SparkAuthCapabilities {
  return { localCredentials: 'Disabled', externalProviders: [github], ...overrides } as SparkAuthCapabilities;
}

async function setup(
  reported: SparkAuthCapabilities,
  declared: SparkExternalProviderPresentation[] = [],
) {
  const auth: any = {
    capabilities: vi.fn(async () => reported),
    loginWithProvider: vi.fn().mockResolvedValue(undefined),
  };

  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      // eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic props
      provideNoopAnimations(),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      { provide: SPARK_AUTH_ROUTE_PATHS, useValue: { signIn: '/sign-in' } },
      { provide: SPARK_EXTERNAL_PROVIDERS, useValue: declared },
    ],
  });

  const harness = await RouterTestingHarness.create();
  const host = await harness.navigateByUrl('/sign-in', HostComponent);
  harness.detectChanges();
  return { harness, host, auth };
}

const buttons = (harness: RouterTestingHarness, selector = 'button') =>
  Array.from(harness.routeNativeElement!.querySelectorAll(selector)) as HTMLElement[];

describe('SparkSignInComponent projection', () => {
  it('renders the projected template once per provider', async () => {
    const { harness } = await setup(capabilities({ externalProviders: [github, google] }));

    expect(buttons(harness, 'button.custom').map((b) => b.textContent!.trim()))
      .toEqual(['Continue with GitHub', 'Continue with Google']);
  });

  it('signs in with the server-reported scheme through the context closure', async () => {
    // The point of passing a closure rather than the scheme: the consumer never names it, so #303's
    // failure mode is unreachable by construction.
    const { harness, auth } = await setup(capabilities({ externalProviders: [google] }));

    buttons(harness, 'button.custom')[0].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('Google', { returnUrl: undefined });
  });

  it('threads returnUrl into the sign-in call', async () => {
    // A23b. WebhooksDemo passed returnUrl: '/github-projects' to its hand-rolled call; the component
    // had no equivalent, so adopting it would have silently changed where users land.
    const { harness, host, auth } = await setup(capabilities({ externalProviders: [github] }));
    host.returnUrl.set('/github-projects');
    harness.detectChanges();

    buttons(harness, 'button.custom')[0].click();

    expect(auth.loginWithProvider).toHaveBeenCalledWith('GitHub', { returnUrl: '/github-projects' });
  });

  it('falls back to the default button when no template is projected', async () => {
    // A21: with nothing customised, rendering is what it always was.
    const { harness, host } = await setup(capabilities({ externalProviders: [github] }));
    host.projected.set(false);
    harness.detectChanges();

    expect(buttons(harness, 'button.custom')).toHaveLength(0);
    expect(buttons(harness)[0].textContent!.trim()).toBe('GitHub');
  });

  it('does not render the template for a provider the server did not report', async () => {
    // A declaration decorates; it cannot conjure a provider. Letting the client declare providers is
    // what /spark/auth/capabilities exists to prevent.
    const { harness } = await setup(
      capabilities({ externalProviders: [github] }),
      [{ scheme: 'Facebook', displayName: 'Facebook' }],
    );

    expect(buttons(harness, 'button.custom').map((b) => b.textContent!.trim()))
      .toEqual(['Continue with GitHub']);
  });

  it('applies a declared display name and ordering to what the server reported', async () => {
    const { harness } = await setup(
      capabilities({ externalProviders: [github, google] }),
      [{ scheme: 'google', displayName: 'Google Workspace', order: 1 }],
    );

    // Declared first, then the rest in the server's order — so adding a provider server-side appends
    // a button instead of reshuffling the ones already there.
    expect(buttons(harness, 'button.custom').map((b) => b.textContent!.trim()))
      .toEqual(['Continue with Google Workspace', 'Continue with GitHub']);
  });

  it('re-stamps the projected template when providers resolve after first render', async () => {
    // S7: OnPush and zoneless. `capabilities` settles after the first change detection, so the list
    // is written to a signal post-render — with no markForCheck and no zone to notice.
    let resolve!: (value: SparkAuthCapabilities) => void;
    const pending = new Promise<SparkAuthCapabilities>((r) => (resolve = r));

    const auth: any = {
      capabilities: vi.fn(() => pending),
      loginWithProvider: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        // eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic props
        provideNoopAnimations(),
        { provide: SparkAuthService, useValue: auth },
        { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
        { provide: SPARK_AUTH_ROUTE_PATHS, useValue: { signIn: '/sign-in' } },
        { provide: SPARK_EXTERNAL_PROVIDERS, useValue: [] },
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sign-in', HostComponent);
    harness.detectChanges();

    expect(buttons(harness, 'button.custom')).toHaveLength(0);

    resolve(capabilities({ externalProviders: [github, google] }));
    await pending;
    harness.detectChanges();

    expect(buttons(harness, 'button.custom')).toHaveLength(2);
  });
});
