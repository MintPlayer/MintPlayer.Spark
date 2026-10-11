import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCopyDirective } from '@mintplayer/ng-bootstrap/copy';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** The three proofs of PRD F3 for a site, exactly as the server checks them (SeoTester.Sites.OwnershipProof). */
export function seoVerificationProofs(origin: string, host: string, token: string) {
  const value = `seotester-site-verification=${token}`;
  return {
    dnsName: `_seotester.${host}`,
    dnsValue: value,
    fileUrl: `${origin.replace(/\/+$/, '')}/.well-known/seotester-verification.txt`,
    fileContent: value,
    metaTag: `<meta name="seotester-site-verification" content="${token}">`,
  };
}

/**
 * `seo-verification`, the full-width renderer on `Site.VerificationToken` (PRD F3, D18): verified, with how and when and
 * how the re-checks go; otherwise the three ways to prove ownership with their exact values and a copy button each, then
 * the *Verify ownership* action.
 */
@Component({
  selector: 'app-seo-verification',
  imports: [NgTemplateOutlet, BsAlertComponent, BsCopyDirective, TranslateKeyPipe],
  template: `
    @let s = state();
    @if (s.verified) {
      <div data-verified>
        <span class="text-success fw-semibold"><i class="bi bi-patch-check-fill"></i> {{ 'seoUi.verify.verified' | t }}</span>
        @if (s.method) { <span class="ms-1">({{ ('seoUi.verify.method.' + s.method) | t }})</span> }
        @if (s.checkedAt) { <div class="small text-body-secondary">{{ 'seoUi.verify.lastChecked' | t }} {{ s.checkedAt }}</div> }
        @if (s.failures > 0) {
          <bs-alert [type]="colors.warning" class="mt-2 mb-0" data-failures>{{ failuresText() }}</bs-alert>
        }
      </div>
    } @else if (s.proofs) {
      <div data-unverified>
        <p class="mb-2">{{ 'seoUi.verify.intro' | t }}</p>
        @if (s.checkedAt) { <p class="small text-body-secondary">{{ 'seoUi.verify.notFoundAt' | t }} {{ s.checkedAt }}</p> }
        <ol class="mb-2 ps-3">
          <li class="mb-2" data-method="dns">
            <strong>{{ 'seoUi.verify.method.Dns' | t }}</strong>
            <ng-container *ngTemplateOutlet="copyLine; context: { label: ('seoUi.verify.recordName' | t), value: s.proofs.dnsName }" />
            <ng-container *ngTemplateOutlet="copyLine; context: { label: ('seoUi.verify.recordValue' | t), value: s.proofs.dnsValue }" />
          </li>
          <li class="mb-2" data-method="file">
            <strong>{{ 'seoUi.verify.method.WellKnownFile' | t }}</strong>
            <ng-container *ngTemplateOutlet="copyLine; context: { label: ('seoUi.verify.fileAt' | t), value: s.proofs.fileUrl }" />
            <ng-container *ngTemplateOutlet="copyLine; context: { label: ('seoUi.verify.fileContent' | t), value: s.proofs.fileContent }" />
          </li>
          <li data-method="meta">
            <strong>{{ 'seoUi.verify.method.MetaTag' | t }}</strong>
            <ng-container *ngTemplateOutlet="copyLine; context: { label: ('seoUi.verify.inHead' | t), value: s.proofs.metaTag }" />
          </li>
        </ol>
        <p class="small mb-0">{{ 'seoUi.verify.then' | t }}</p>
      </div>
    } @else {
      <span class="text-muted">—</span>
    }

    <ng-template #copyLine let-label="label" let-value="value">
      <div class="d-flex flex-wrap align-items-center gap-2 small mt-1">
        <span class="text-body-secondary">{{ label }}</span>
        <code class="user-select-all text-break">{{ value }}</code>
        <button type="button" class="btn btn-outline-secondary btn-sm py-0" [bsCopy]="value" [copiedAnnouncement]="'seoUi.copied' | t">
          <i class="bi bi-clipboard"></i> {{ 'seoUi.copy' | t }}
        </button>
      </div>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoVerificationComponent {
  readonly value = input<unknown>();
  readonly formData = input<Record<string, unknown>>({});

  private readonly lang = inject(SparkLanguageService);
  protected readonly colors = Color;

  protected readonly state = computed(() => {
    const data = this.formData();
    const token = typeof this.value() === 'string' ? (this.value() as string) : '';
    const origin = typeof data['Origin'] === 'string' ? (data['Origin'] as string) : '';
    const host = typeof data['Host'] === 'string' && data['Host'] ? (data['Host'] as string) : hostOf(origin);
    const checked = data['VerificationCheckedAtUtc'];
    return {
      verified: data['IsVerified'] === true,
      method: typeof data['VerificationMethod'] === 'string' ? (data['VerificationMethod'] as string) : null,
      checkedAt: checked ? this.format(checked) : null,
      failures: Number(data['VerificationFailures'] ?? 0) || 0,
      proofs: token && origin && host ? seoVerificationProofs(origin, host, token) : null,
    };
  });

  protected readonly failuresText = computed(() =>
    this.lang.t('seoUi.verify.failures', '{0} re-checks failed.').replace('{0}', String(this.state().failures)));

  private format(value: unknown): string {
    const date = new Date(String(value));
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString(this.lang.language());
  }
}

function hostOf(origin: string): string {
  try { return new URL(origin).hostname; } catch { return ''; }
}
