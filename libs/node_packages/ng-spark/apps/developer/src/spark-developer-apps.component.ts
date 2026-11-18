import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import {
  SparkAppsOwnerPickerComponent,
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsLinks,
  injectAppsText,
  type SparkApp,
  type SparkAppFields,
  type SparkWebhookCatalog,
  type SparkWebhookOwner,
} from '@mintplayer/ng-spark/apps/core';
import { SparkAppFormComponent } from './spark-app-form.component';

/**
 * The Apps the user and their organizations registered (D5, D29), and the form to register one. A new App's
 * webhook secret is shown once; its settings page then makes the keys it signs in with.
 */
@Component({
  selector: 'spark-developer-apps',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent,
    SparkAppsOwnerPickerComponent, SparkAppsTextPipe, SparkAppFormComponent,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ 'apps.title' | sparkApps }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'apps.intro' | sparkApps }}</p>
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          <spark-apps-owner-picker [owners]="owners()" [selected]="owner()" (selectedChange)="choose($event)" />

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else {
            @for (app of apps(); track app.slug) {
              <div class="border rounded p-3 mb-2 d-flex justify-content-between align-items-center spark-developer-app" [attr.data-slug]="app.slug">
                <div>
                  <a [routerLink]="links.app(app.slug)" class="fw-semibold">{{ app.name }}</a>
                  <small class="text-muted ms-2"><code>{{ app.slug }}</code></small>
                  @if (app.description) { <div class="text-muted small">{{ app.description }}</div> }
                </div>
                <bs-badge [color]="app.public ? 'success' : 'secondary'">{{ (app.public ? 'apps.public' : 'apps.private') | sparkApps }}</bs-badge>
              </div>
            } @empty {
              <p class="text-muted spark-developer-apps-empty">{{ 'apps.none' | sparkApps }}</p>
            }

            @if (catalog(); as catalog) {
              @if (registering()) {
                <hr class="my-4" />
                <h5>{{ 'apps.registerTitle' | sparkApps }}</h5>
                <spark-app-form [catalog]="catalog" [isNew]="true" [busy]="busy()" (saved)="register($event)" />
              } @else if (owner()) {
                <button type="button" class="btn btn-primary mt-3 spark-developer-apps-new" (click)="registering.set(true)">{{ 'apps.new' | sparkApps }}</button>
              }
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkDeveloperAppsComponent {
  private readonly service = inject(SparkAppsService);
  private readonly secrets = inject(SparkSecretDialogService);
  private readonly router = inject(Router);
  private readonly text = injectAppsText();
  protected readonly links = injectAppsLinks();

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly registering = signal(false);
  readonly owners = signal<SparkWebhookOwner[]>([]);
  readonly owner = signal<SparkWebhookOwner | null>(null);
  readonly catalog = signal<SparkWebhookCatalog | null>(null);
  readonly apps = signal<SparkApp[]>([]);
  readonly error = signal('');

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
    this.catalog.set(catalog.value);
    await this.choose(owners.value[0] ?? null);
  }

  async choose(owner: SparkWebhookOwner | null): Promise<void> {
    this.owner.set(owner);
    this.registering.set(false);
    if (!owner) {
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    const result = await this.service.apps(owner);
    if (result.ok) this.apps.set(result.value);
    else this.error.set(this.text('loadFailed'));
    this.loading.set(false);
  }

  async register(fields: SparkAppFields): Promise<void> {
    const owner = this.owner();
    if (!owner) return;
    this.busy.set(true);
    this.error.set('');
    const result = await this.service.createApp({ ...fields, ownerKind: owner.kind, ownerKey: owner.key });
    this.busy.set(false);
    if (!result.ok) {
      this.error.set(result.error ?? this.text('failed'));
      return;
    }
    if (result.value.webhookSecret) {
      this.secrets.show({ title: this.text('webhooks.secretTitle'), message: this.text('webhooks.secretMessage'), value: result.value.webhookSecret });
    }
    await this.router.navigateByUrl(this.links.app(result.value.app.slug));
  }
}
