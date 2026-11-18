import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SparkAppsDatePipe,
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsLinks,
  injectAppsText,
  type SparkWebhookDelivery,
  type SparkWebhookDeliveryStatus,
} from '@mintplayer/ng-spark/apps/core';

/** The server's page size (`ListWebhookDeliveries`): a full page means there may be older ones. */
const PAGE_SIZE = 50;

type BadgeColor = 'secondary' | 'success' | 'danger';

const STATUS_COLORS: Record<SparkWebhookDeliveryStatus, BadgeColor> = {
  Pending: 'secondary',
  Succeeded: 'success',
  Failed: 'danger',
};

/**
 * A webhook's delivery log (D23), newest first: each delivery's payload and attempts, and redeliver, which sends
 * it again under the same `webhook-id` so a receiver that already processed it ignores it.
 */
@Component({
  selector: 'spark-webhook-deliveries',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, SparkAppsTextPipe, SparkAppsDatePipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ 'deliveries.title' | sparkApps }}</h3></bs-card-header>
        <div class="p-4">
          <a [routerLink]="links.webhooks()" class="d-inline-block mb-3">{{ 'deliveries.back' | sparkApps }}</a>
          @if (notice()) { <bs-alert [color]="'success'" class="mb-3 d-block spark-apps-notice">{{ notice() }}</bs-alert> }
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          @for (delivery of deliveries(); track delivery.id) {
            <div class="border rounded p-2 mb-2 spark-delivery" [attr.data-id]="delivery.id">
              <button type="button" class="btn btn-link p-0 text-start w-100 text-decoration-none spark-delivery-toggle" (click)="toggle(delivery.id)"
                      [attr.aria-expanded]="open() === delivery.id">
                <bs-badge [color]="statusColor(delivery.status)" class="me-2 spark-delivery-status">{{ 'deliveries.status.' + delivery.status | sparkApps }}</bs-badge>
                <code>{{ delivery.eventType }}</code>
                <small class="text-muted ms-2">{{ delivery.createdUtc | sparkAppsDate }}</small>
                @if (delivery.redeliveryOf) { <small class="text-muted ms-2">{{ 'deliveries.redelivery' | sparkApps }}</small> }
              </button>
              @if (open() === delivery.id) {
                <div class="mt-2">
                  <small class="text-muted d-block">webhook-id: <code>{{ delivery.messageId }}</code></small>
                  <pre class="bg-body-tertiary p-2 mt-2 mb-2 spark-delivery-payload">{{ pretty(delivery.payload) }}</pre>
                  @for (attempt of delivery.attempts; track $index) {
                    <div class="small spark-delivery-attempt">
                      {{ attempt.atUtc | sparkAppsDate }} ·
                      {{ attempt.statusCode ?? attempt.error ?? '—' }} ·
                      {{ attempt.durationMs }} ms
                      @if (attempt.responseBody) { <pre class="mb-1 small text-muted">{{ attempt.responseBody }}</pre> }
                    </div>
                  } @empty {
                    <div class="small text-muted">{{ 'deliveries.noAttempts' | sparkApps }}</div>
                  }
                  <button type="button" class="btn btn-sm btn-outline-primary mt-2 spark-delivery-redeliver" [disabled]="busy()" (click)="redeliver(delivery)">
                    {{ 'deliveries.redeliver' | sparkApps }}
                  </button>
                </div>
              }
            </div>
          } @empty {
            @if (!loading()) { <p class="text-muted spark-deliveries-empty">{{ 'deliveries.none' | sparkApps }}</p> }
          }

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (more()) {
            <button type="button" class="btn btn-outline-secondary spark-deliveries-more" (click)="loadMore()">{{ 'deliveries.older' | sparkApps }}</button>
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkWebhookDeliveriesComponent {
  private readonly service = inject(SparkAppsService);
  private readonly text = injectAppsText();
  private readonly endpointId = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';
  protected readonly links = injectAppsLinks();

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly deliveries = signal<SparkWebhookDelivery[]>([]);
  readonly more = signal(false);
  readonly open = signal<string | null>(null);
  readonly error = signal('');
  readonly notice = signal('');

  constructor() {
    void this.load(null);
  }

  private async load(after: string | null): Promise<void> {
    this.loading.set(true);
    const result = await this.service.deliveries(this.endpointId, after);
    if (result.ok) {
      this.deliveries.update(list => (after ? [...list, ...result.value] : result.value));
      this.more.set(result.value.length === PAGE_SIZE);
    } else {
      this.error.set(result.status === 404 ? this.text('deliveries.notFound') : this.text('loadFailed'));
      this.more.set(false);
    }
    this.loading.set(false);
  }

  loadMore(): Promise<void> {
    const last = this.deliveries().at(-1);
    return this.load(last?.id ?? null);
  }

  toggle(id: string): void {
    this.open.update(open => (open === id ? null : id));
  }

  async redeliver(delivery: SparkWebhookDelivery): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await this.service.redeliver(delivery.id);
    if (result.ok) {
      this.notice.set(this.text('deliveries.redelivered'));
      this.open.set(null);
      await this.load(null);
    } else {
      this.error.set(result.error ?? this.text('failed'));
    }
    this.busy.set(false);
  }

  protected statusColor(status: SparkWebhookDeliveryStatus): BadgeColor {
    return STATUS_COLORS[status] ?? 'secondary';
  }

  /** The payload indented when it is JSON (it always is, unless the log was cut short). */
  protected pretty(payload: string): string {
    try {
      return JSON.stringify(JSON.parse(payload), null, 2);
    } catch {
      return payload;
    }
  }
}
