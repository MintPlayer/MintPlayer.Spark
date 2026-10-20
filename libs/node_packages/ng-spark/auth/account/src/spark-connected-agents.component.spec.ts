import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import { SparkAgentGrantsOverview } from '@mintplayer/ng-spark/auth/models';
import { withConnectedAgents } from '@mintplayer/ng-spark/auth/routes';
import { SparkConnectedAgentsComponent } from './spark-connected-agents.component';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

function overview(extra: Partial<SparkAgentGrantsOverview> = {}): SparkAgentGrantsOverview {
  return {
    personalAccessTokens: true,
    defaultTokenLifetimeDays: 90,
    maxTokenLifetimeDays: 365,
    tokenPermissions: 'Read, Write',
    types: [
      { id: 'c0000000-0000-4000-8000-00000000c001', name: 'Contract', label: 'Contracts', permissions: 'Read, Write', queries: ['All contracts'] },
      { id: 'c0000000-0000-4000-8000-00000000c002', name: 'Customer', label: 'Customers', permissions: 'Read', queries: [] },
    ],
    grants: [{
      key: 'pat/0123456789abcdef0123456789abcdef',
      kind: 'PersonalAccessToken',
      name: 'CI job',
      clientId: null,
      verifiedHost: null,
      permissions: 'Read',
      types: [{ typeId: 'c0000000-0000-4000-8000-00000000c001', permissions: 'Read' }],
      maxPermissions: 'Read, Write',
      createdAt: '2026-10-01T10:00:00Z',
      expiresAt: '2026-12-30T10:00:00Z',
      lastUsedAt: null,
      status: 'active',
    }],
    applications: [],
    ...extra,
  };
}

async function render(auth: Record<string, unknown>): Promise<ComponentFixture<SparkConnectedAgentsComponent>> {
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
    ],
  });
  const fixture = TestBed.createComponent(SparkConnectedAgentsComponent);
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
  return fixture;
}

const element = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

describe('SparkConnectedAgentsComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('lists a grant with what it may see, and labels a revoked token', async () => {
    const data = overview();
    data.grants.push({ ...data.grants[0], key: 'pat/ffffffffffffffffffffffffffffffff', name: 'old', status: 'revoked' });
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: true, value: data }) });

    const grants = element(fixture).querySelectorAll('.spark-agent-grant');
    expect(grants.length).toBe(2);
    expect(grants[0].textContent).toContain('CI job');
    expect(grants[0].querySelector('.spark-agent-scope')?.textContent).toContain('Contracts');
    expect(grants[1].querySelector('.spark-agent-status')?.textContent).toContain('auth.agentStatus.revoked');
    expect(grants[1].querySelector('.spark-agent-edit')).toBeNull();
  });

  it('offers an edit, the only way to allow it again, for a disconnected external agent', async () => {
    const data = overview();
    data.grants = [{ ...data.grants[0], key: 'ext/0123456789abcdef0123456789abcdef', kind: 'ExternalAuthorizationServer', expiresAt: null, status: 'disconnected' }];
    const updateAgentGrant = vi.fn().mockResolvedValue({ success: true });
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: true, value: data }), updateAgentGrant });

    const grant = element(fixture).querySelector('.spark-agent-grant')!;
    expect(grant.querySelector('.spark-agent-status')?.textContent).toContain('auth.agentStatus.disconnected');
    expect(grant.querySelector('.spark-agent-revoke')).toBeNull();
    (grant.querySelector('.spark-agent-edit') as HTMLButtonElement).click();
    await fixture.componentInstance.save(fixture.componentInstance.overview()!.grants[0]);
    expect(updateAgentGrant).toHaveBeenCalledWith({ key: 'ext/0123456789abcdef0123456789abcdef', permissions: 'Read', types: [{ typeId: 'c0000000-0000-4000-8000-00000000c001', permissions: 'Read' }] });
  });

  it('creates a token and shows it once', async () => {
    const agentGrants = vi.fn().mockResolvedValue({ success: true, value: overview() });
    const createAgentToken = vi.fn().mockResolvedValue({ success: true, value: { token: 'spat_secret', key: 'pat/x', expiresAt: '2027-01-01T00:00:00Z' } });
    const updateAgentGrant = vi.fn().mockResolvedValue({ success: true });
    const fixture = await render({ agentGrants, createAgentToken, updateAgentGrant });
    const component = fixture.componentInstance;

    component.startToken();
    component.tokenName.set('laptop');
    component.setEverything(false);
    component.setType(component.overview()!.types[1], true);
    await component.createToken();
    fixture.detectChanges();

    expect(createAgentToken).toHaveBeenCalledWith({ name: 'laptop', expiresInDays: 90, permissions: 'Read', types: [{ typeId: 'c0000000-0000-4000-8000-00000000c002', permissions: 'Read' }] });
    expect(element(fixture).querySelector('.spark-agent-token')?.textContent).toContain('spat_secret');

    await component.load();
    fixture.detectChanges();
    expect(element(fixture).querySelector('.spark-agent-token')?.textContent).toContain('spat_secret');
    component.edit(component.overview()!.grants[0]);
    await component.save(component.overview()!.grants[0]);
    fixture.detectChanges();
    expect(element(fixture).querySelector('.spark-agent-token')).toBeNull();
  });

  it('caps changes at what each chosen type allows and sends everything as null', async () => {
    const updateAgentGrant = vi.fn().mockResolvedValue({ success: true });
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: true, value: overview() }), updateAgentGrant });
    const component = fixture.componentInstance;
    const grant = component.overview()!.grants[0];

    component.edit(grant);
    component.setChanges(true);
    component.setType(component.overview()!.types[1], true);
    await component.save(grant);
    expect(updateAgentGrant).toHaveBeenLastCalledWith({
      key: grant.key,
      permissions: 'Read, Write',
      types: [{ typeId: 'c0000000-0000-4000-8000-00000000c001', permissions: 'Read, Write' }, { typeId: 'c0000000-0000-4000-8000-00000000c002', permissions: 'Read' }],
    });

    component.edit(grant);
    component.setEverything(true);
    await component.save(grant);
    expect(updateAgentGrant).toHaveBeenLastCalledWith({ key: grant.key, permissions: 'Read', types: null });
  });

  it('offers no token form when the server does not accept tokens, and no changes beyond the limit', async () => {
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: true, value: overview({ personalAccessTokens: false }) }) });

    expect(element(fixture).querySelector('.spark-agent-token-new')).toBeNull();
  });

  it('says the page is unavailable when the server does not serve it', async () => {
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: false, status: 404 }) });

    expect(element(fixture).querySelector('.spark-agents-error')?.textContent).toContain('auth.accountUnavailable');
  });

  it('styles the token inputs as form controls', async () => {
    // ng-bootstrap adds .form-control through BsFormControlDirective, which only matches inputs
    // in templates that import it; <bs-form> alone leaves them unstyled.
    const fixture = await render({ agentGrants: vi.fn().mockResolvedValue({ success: true, value: overview() }) });
    fixture.componentInstance.startToken();
    fixture.detectChanges();

    expect(element(fixture).querySelector('#sparkAgentTokenName')?.classList).toContain('form-control');
    expect(element(fixture).querySelector('#sparkAgentTokenDays')?.classList).toContain('form-control');
  });
});

describe('withConnectedAgents', () => {
  it('mounts account/agents behind the sign-in guard and links it from the overview', () => {
    const feature = withConnectedAgents();

    expect(feature.paths).toEqual({ agents: '/account/agents' });
    expect(feature.children[0].path).toBe('account/agents');
    expect(feature.children[0].canActivate?.length).toBe(1);
    expect(feature.accountLinks?.[0]).toMatchObject({ key: 'agents', path: '/account/agents' });
  });
});
