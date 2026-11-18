import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import {
  SparkAppsDatePipe,
  SparkAppsOwnerPickerComponent,
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsLinks,
  injectAppsText,
  type SparkAppsResult,
  type SparkWebhookEndpoint,
  type SparkWebhookEventInfo,
  type SparkWebhookOwner,
} from '@mintplayer/ng-spark/apps/core';

/** The form's state: a new webhook, or the one being edited. */
interface WebhookDraft {
  id: string | null;
  url: string;
  description: string;
  events: string[];
}

/**
 * The user's and their organizations' webhooks (#691 D7, D11): add one (its secret is shown once), choose its
 * events, ping it, rotate its secret, re-enable it after the breaker disabled it, delete it, and open its delivery
 * log. An App's own webhook is managed on the App's page instead.
 */
@Component({
  selector: 'spark-webhooks',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule, RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent,
    BsSpinnerComponent, SparkAppsOwnerPickerComponent, SparkAppsTextPipe, SparkAppsDatePipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ 'webhooks.title' | sparkApps }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'webhooks.intro' | sparkApps }}</p>
          @if (notice()) { <bs-alert [color]="'success'" class="mb-3 d-block spark-apps-notice">{{ notice() }}</bs-alert> }
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          <spark-apps-owner-picker [owners]="owners()" [selected]="owner()" (selectedChange)="choose($event)" />

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else {
            @for (endpoint of endpoints(); track endpoint.id) {
              <div class="border rounded p-3 mb-3 spark-webhook" [attr.data-id]="endpoint.id">
                <div class="d-flex flex-wrap justify-content-between gap-2">
                  <div>
                    <code class="spark-webhook-url">{{ endpoint.url }}</code>
                    @if (endpoint.description) { <div class="text-muted">{{ endpoint.description }}</div> }
                  </div>
                  @if (endpoint.enabled) {
                    <bs-badge [color]="'success'" class="align-self-start spark-webhook-state" data-state="enabled">{{ 'webhooks.enabled' | sparkApps }}</bs-badge>
                  } @else {
                    <bs-badge [color]="'danger'" class="align-self-start spark-webhook-state" data-state="disabled">{{ 'webhooks.disabled' | sparkApps }}</bs-badge>
                  }
                </div>
                @if (!endpoint.enabled && endpoint.disabledReason) {
                  <small class="d-block text-danger">{{ endpoint.disabledReason }}</small>
                }
                @if (endpoint.failingSinceUtc) {
                  <small class="d-block text-warning">{{ 'webhooks.failingSince' | sparkApps: (endpoint.failingSinceUtc | sparkAppsDate) }}</small>
                }
                @if (endpoint.lastSuccessUtc) {
                  <small class="d-block text-muted">{{ 'webhooks.lastSuccess' | sparkApps: (endpoint.lastSuccessUtc | sparkAppsDate) }}</small>
                }
                <small class="d-block text-muted mb-2">{{ 'webhooks.events' | sparkApps }} {{ endpoint.events.join(', ') }}</small>
                <div class="d-flex flex-wrap gap-2">
                  <a class="btn btn-sm btn-outline-primary spark-webhook-deliveries" [routerLink]="links.deliveries(endpoint.id)">{{ 'webhooks.deliveries' | sparkApps }}</a>
                  <button type="button" class="btn btn-sm btn-outline-secondary spark-webhook-edit" [disabled]="busy()" (click)="edit(endpoint)">{{ 'edit' | sparkApps }}</button>
                  <button type="button" class="btn btn-sm btn-outline-secondary spark-webhook-ping" [disabled]="busy()" (click)="ping(endpoint)">{{ 'webhooks.ping' | sparkApps }}</button>
                  <button type="button" class="btn btn-sm btn-outline-secondary spark-webhook-rotate" [disabled]="busy()" (click)="rotate(endpoint)">{{ 'webhooks.rotate' | sparkApps }}</button>
                  @if (!endpoint.enabled) {
                    <button type="button" class="btn btn-sm btn-outline-success spark-webhook-enable" [disabled]="busy()" (click)="enable(endpoint)">{{ 'webhooks.enable' | sparkApps }}</button>
                  }
                  <button type="button" class="btn btn-sm btn-outline-danger spark-webhook-delete" [disabled]="busy()" (click)="remove(endpoint)">{{ 'delete' | sparkApps }}</button>
                </div>
              </div>
            } @empty {
              <p class="text-muted spark-webhooks-empty">{{ 'webhooks.none' | sparkApps }}</p>
            }

            @if (draft(); as draft) {
              <hr class="my-4" />
              <h5>{{ (draft.id ? 'webhooks.editTitle' : 'webhooks.addTitle') | sparkApps }}</h5>
              <bs-form (submitted)="save()">
                <div class="mb-3">
                  <label for="sparkWebhookUrl" class="form-label">{{ 'webhooks.url' | sparkApps }}</label>
                  <input type="url" id="sparkWebhookUrl" name="url" required [ngModel]="draft.url" (ngModelChange)="patch({ url: $event })" placeholder="https://" />
                </div>
                <div class="mb-3">
                  <label for="sparkWebhookDescription" class="form-label">{{ 'webhooks.description' | sparkApps }}</label>
                  <input type="text" id="sparkWebhookDescription" name="description" [ngModel]="draft.description" (ngModelChange)="patch({ description: $event })" />
                </div>
                <fieldset class="mb-3">
                  <legend class="form-label fs-6">{{ 'webhooks.chooseEvents' | sparkApps }}</legend>
                  @for (event of catalog(); track event.name) {
                    <bs-checkbox [type]="'checkbox'" [name]="'event-' + event.name" [checked]="draft.events.includes(event.name)"
                                 (checkedChange)="toggle(event.name, $event === true)">
                      <code>{{ event.name }}</code> <span class="text-muted">{{ event.summary }}</span>
                    </bs-checkbox>
                  }
                </fieldset>
                <div class="d-flex gap-2">
                  <button type="submit" class="btn btn-primary spark-webhook-save" [disabled]="busy() || !draft.url || !draft.events.length">
                    @if (busy()) { <bs-spinner class="me-1" /> }
                    {{ (draft.id ? 'save' : 'webhooks.add') | sparkApps }}
                  </button>
                  @if (draft.id) {
                    <button type="button" class="btn btn-outline-secondary" (click)="newDraft()">{{ 'cancel' | sparkApps }}</button>
                  }
                </div>
              </bs-form>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkWebhooksComponent {
  private readonly service = inject(SparkAppsService);
  private readonly secrets = inject(SparkSecretDialogService);
  private readonly text = injectAppsText();
  protected readonly links = injectAppsLinks();

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly owners = signal<SparkWebhookOwner[]>([]);
  readonly owner = signal<SparkWebhookOwner | null>(null);
  readonly catalog = signal<SparkWebhookEventInfo[]>([]);
  readonly endpoints = signal<SparkWebhookEndpoint[]>([]);
  readonly draft = signal<WebhookDraft | null>(null);
  readonly error = signal('');
  readonly notice = signal('');

  constructor() {
    void this.init();
  }

  private async init(): Promise<void> {
    const [owners, catalog] = await Promise.all([this.service.owners(), this.service.catalog()]);
    if (!owners.ok || !catalog.ok) {
      this.error.set(this.text('loadFailed'));
      this.loading.set(false);
      return;
    }
    this.owners.set(owners.value);
    this.catalog.set(catalog.value.events);
    await this.choose(owners.value[0] ?? null);
  }

  async choose(owner: SparkWebhookOwner | null): Promise<void> {
    this.owner.set(owner);
    this.newDraft();
    await this.reload();
  }

  private async reload(): Promise<void> {
    const owner = this.owner();
    if (!owner) {
      this.endpoints.set([]);
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    const result = await this.service.endpoints(owner);
    if (result.ok) this.endpoints.set(result.value);
    else this.error.set(this.text('loadFailed'));
    this.loading.set(false);
  }

  newDraft(): void {
    this.draft.set({ id: null, url: '', description: '', events: [] });
  }

  edit(endpoint: SparkWebhookEndpoint): void {
    this.draft.set({ id: endpoint.id, url: endpoint.url, description: endpoint.description ?? '', events: [...endpoint.events] });
  }

  patch(change: Partial<WebhookDraft>): void {
    this.draft.update(draft => draft && { ...draft, ...change });
  }

  toggle(event: string, on: boolean): void {
    this.draft.update(draft => draft && {
      ...draft,
      events: on ? [...new Set([...draft.events, event])] : draft.events.filter(e => e !== event),
    });
  }

  async save(): Promise<void> {
    const draft = this.draft();
    const owner = this.owner();
    if (!draft || !owner) return;
    const description = draft.description.trim() || null;
    if (draft.id) {
      await this.run(this.service.updateEndpoint({ id: draft.id, url: draft.url, description, events: draft.events }), () => {
        this.newDraft();
        return this.text('webhooks.saved');
      });
    } else {
      await this.run(this.service.createEndpoint({ ownerKind: owner.kind, ownerKey: owner.key, url: draft.url, description, events: draft.events }), created => {
        this.newDraft();
        this.secrets.show({ title: this.text('webhooks.secretTitle'), message: this.text('webhooks.secretMessage'), value: created.secret });
        return this.text('webhooks.added');
      });
    }
  }

  ping(endpoint: SparkWebhookEndpoint): Promise<void> {
    return this.run(this.service.ping(endpoint.id), () => this.text('webhooks.pinged'));
  }

  rotate(endpoint: SparkWebhookEndpoint): Promise<void> {
    if (!confirm(this.text('webhooks.confirmRotate'))) return Promise.resolve();
    return this.run(this.service.rotateSecret(endpoint.id), rotated => {
      this.secrets.show({ title: this.text('webhooks.secretTitle'), message: this.text('webhooks.rotatedMessage'), value: rotated.secret });
      return this.text('webhooks.rotated');
    });
  }

  enable(endpoint: SparkWebhookEndpoint): Promise<void> {
    return this.run(this.service.enableEndpoint(endpoint.id), () => this.text('webhooks.reenabled'));
  }

  remove(endpoint: SparkWebhookEndpoint): Promise<void> {
    if (!confirm(this.text('webhooks.confirmDelete', endpoint.url))) return Promise.resolve();
    return this.run(this.service.deleteEndpoint(endpoint.id), () => this.text('webhooks.deleted'));
  }

  /** Runs a change, shows its outcome and reloads the list (the server is the truth: no full page reload). */
  private async run<T>(call: Promise<SparkAppsResult<T>>, done: (value: T) => string): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await call;
    if (result.ok) {
      this.notice.set(done(result.value));
      await this.reload();
    } else {
      this.error.set(result.error ?? this.text('failed'));
    }
    this.busy.set(false);
  }
}
