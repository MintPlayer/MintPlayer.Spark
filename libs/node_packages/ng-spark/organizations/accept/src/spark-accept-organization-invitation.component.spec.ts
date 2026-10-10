import { HttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import { SparkAcceptOrganizationInvitationComponent } from './spark-accept-organization-invitation.component';

function configure(post: ReturnType<typeof vi.fn>, token = 'tok', id = 'OrganizationInvitations/1') {
  TestBed.configureTestingModule({
    imports: [SparkAcceptOrganizationInvitationComponent],
    providers: [
      provideRouter([]),
      provideNoopAnimations(),
      { provide: HttpClient, useValue: { post } },
      { provide: SparkAuthTranslationService, useValue: { t: (k: string) => k } },
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: convertToParamMap({ token }), queryParamMap: convertToParamMap({ id }) } },
      },
    ],
  });
  return TestBed.createComponent(SparkAcceptOrganizationInvitationComponent);
}

describe('SparkAcceptOrganizationInvitationComponent', () => {
  it('posts nothing until the user clicks Accept (mail scanners open links)', () => {
    const post = vi.fn();
    const fixture = configure(post);
    fixture.detectChanges();

    expect(post).not.toHaveBeenCalled();
  });

  it('posts the id and token from the link and shows the organization joined', async () => {
    const post = vi.fn().mockReturnValue(of({ accepted: true, organizationId: 'Organizations/a', organization: 'Acme', problem: null }));
    const fixture = configure(post);

    await fixture.componentInstance.accept();

    expect(post).toHaveBeenCalledWith('/spark/organizations/accept', { invitationId: 'OrganizationInvitations/1', token: 'tok' });
    expect(fixture.componentInstance.joined()?.organization).toBe('Acme');
    expect(fixture.componentInstance.failed()).toBe(false);
  });

  it('shows the one refusal for a refused or failed request', async () => {
    const refused = configure(vi.fn().mockReturnValue(of({ accepted: false, organizationId: null, organization: null, problem: 'x' })));
    await refused.componentInstance.accept();
    expect(refused.componentInstance.failed()).toBe(true);

    TestBed.resetTestingModule();
    const broken = configure(vi.fn().mockReturnValue(throwError(() => new Error('500'))));
    await broken.componentInstance.accept();
    expect(broken.componentInstance.failed()).toBe(true);
    expect(broken.componentInstance.busy()).toBe(false);
  });

  it('says why when the invitation was sent to another address (D43), and the one refusal otherwise', async () => {
    const otherAddress = configure(vi.fn().mockReturnValue(of({ accepted: false, organizationId: null, organization: null, problem: 'x', reason: 'otherAddress' })));
    await otherAddress.componentInstance.accept();
    otherAddress.detectChanges();
    const shown = (otherAddress.nativeElement as HTMLElement).querySelector('.spark-organization-invalid')?.textContent?.trim();
    expect(shown).toBe('organizations.spa.otherAddress');

    TestBed.resetTestingModule();
    const invalid = configure(vi.fn().mockReturnValue(of({ accepted: false, organizationId: null, organization: null, problem: 'x', reason: 'invalid' })));
    await invalid.componentInstance.accept();
    invalid.detectChanges();
    expect((invalid.nativeElement as HTMLElement).querySelector('.spark-organization-invalid')?.textContent?.trim()).toBe('organizations.spa.invalid');
  });

  it('does not post a link without a token', async () => {
    const post = vi.fn();
    const fixture = configure(post, '');

    await fixture.componentInstance.accept();

    expect(post).not.toHaveBeenCalled();
  });
});
