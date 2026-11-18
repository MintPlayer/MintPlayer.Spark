import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import {
  SparkAppsDatePipe,
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsLinks,
  injectAppsText,
  type SparkApp,
  type SparkAppFields,
  type SparkAppInstallation,
  type SparkAppKey,
  type SparkAppsResult,
  type SparkWebhookCatalog,
} from '@mintplayer/ng-spark/apps/core';
import { SparkAppFormComponent } from './spark-app-form.component';

/**
 * One App's settings (D29, D30): its client id and install link, its settings, the keys it signs in with (a new
 * private key is shown once; the site keeps only the public half), where it is installed, and deleting it.
 */
@Component({
  selector: 'spark-app-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent,
    SparkAppsTextPipe, SparkAppsDatePipe, SparkAppFormComponent,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ app()?.name ?? ('apps.title' | sparkApps) }}</h3></bs-card-header>
        <div class="p-4">
          <a [routerLink]="links.developerApps()" class="d-inline-block mb-3">{{ 'apps.back' | sparkApps }}</a>
          @if (notice()) { <bs-alert [color]="'success'" class="mb-3 d-block spark-apps-notice">{{ notice() }}</bs-alert> }
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (app(); as app) {
            <dl class="mb-4">
              <dt>{{ 'app.clientId' | sparkApps }}</dt>
              <dd><code class="spark-app-client-id">{{ app.clientId }}</code></dd>
              <dt>{{ 'app.installLink' | sparkApps }}</dt>
              <dd>
                <a [routerLink]="links.install(app.slug)" class="spark-app-install-link">{{ installUrl() }}</a>
                @if (!app.public) { <small class="d-block text-muted">{{ 'app.privateHint' | sparkApps }}</small> }
              </dd>
            </dl>

            @if (catalog(); as catalog) {
              <spark-app-form [catalog]="catalog" [value]="app" [busy]="busy()" (saved)="save($event)" />
            }

            <hr class="my-4" />
            <h5>{{ 'app.keys' | sparkApps }}</h5>
            <p class="text-muted">{{ 'app.keysHint' | sparkApps }}</p>
            @for (key of app.keys; track key.keyId) {
              <div class="d-flex justify-content-between align-items-center border rounded p-2 mb-2 spark-app-key" [attr.data-key]="key.keyId">
                <div>
                  <code>{{ key.fingerprint }}</code>
                  <small class="text-muted ms-2">{{ key.createdUtc | sparkAppsDate }}</small>
                </div>
                <button type="button" class="btn btn-sm btn-outline-danger spark-app-key-delete" [disabled]="busy()" (click)="deleteKey(app, key)">{{ 'delete' | sparkApps }}</button>
              </div>
            } @empty {
              <p class="text-muted spark-app-no-keys">{{ 'app.noKeys' | sparkApps }}</p>
            }
            <button type="button" class="btn btn-outline-primary spark-app-key-generate" [disabled]="busy()" (click)="generateKey(app)">{{ 'app.generateKey' | sparkApps }}</button>

            <hr class="my-4" />
            <h5>{{ 'app.installations' | sparkApps }}</h5>
            @for (installation of installations(); track installation.id) {
              <div class="small mb-1 spark-app-installation">
                {{ installation.targetKind }} <code>{{ installation.targetKey }}</code> · {{ installation.installedUtc | sparkAppsDate }}
                @if (installation.suspended) { <bs-badge [color]="'warning'" class="ms-1">{{ 'installations.suspended' | sparkApps }}</bs-badge> }
                @if (installation.pendingPermissions.length) { <bs-badge [color]="'info'" class="ms-1">{{ 'installations.pending' | sparkApps }}</bs-badge> }
              </div>
            } @empty {
              <p class="text-muted">{{ 'app.notInstalled' | sparkApps }}</p>
            }

            <hr class="my-4" />
            <button type="button" class="btn btn-outline-danger spark-app-delete" [disabled]="busy()" (click)="remove(app)">{{ 'app.delete' | sparkApps }}</button>
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAppSettingsComponent {
  private readonly service = inject(SparkAppsService);
  private readonly secrets = inject(SparkSecretDialogService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly text = injectAppsText();
  private readonly slug = inject(ActivatedRoute).snapshot.paramMap.get('slug') ?? '';
  protected readonly links = injectAppsLinks();

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly app = signal<SparkApp | null>(null);
  readonly catalog = signal<SparkWebhookCatalog | null>(null);
  readonly installations = signal<SparkAppInstallation[]>([]);
  readonly error = signal('');
  readonly notice = signal('');

  /** The link to share: absolute, so it can be sent to others. */
  protected readonly installUrl = computed(() => {
    const app = this.app();
    return app ? (this.document.location?.origin ?? '') + this.links.install(app.slug) : '';
  });

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    const [app, catalog, installations] = await Promise.all([
      this.service.app(this.slug), this.service.catalog(), this.service.appInstallations(this.slug),
    ]);
    // Someone who may only install a public App gets its public view, without a client id: not theirs to manage.
    if (!app.ok || !('clientId' in app.value)) {
      this.error.set(this.text(app.ok || app.status === 404 ? 'app.notFound' : 'loadFailed'));
    } else {
      this.app.set(app.value);
    }
    if (catalog.ok) this.catalog.set(catalog.value);
    if (installations.ok) this.installations.set(installations.value);
    this.loading.set(false);
  }

  save(fields: SparkAppFields): Promise<void> {
    return this.run(this.service.updateApp(fields), updated => {
      this.app.set(updated.app);
      if (updated.webhookSecret) {
        this.secrets.show({ title: this.text('webhooks.secretTitle'), message: this.text('webhooks.secretMessage'), value: updated.webhookSecret });
      }
      return this.text('app.saved');
    });
  }

  generateKey(app: SparkApp): Promise<void> {
    return this.run(this.service.generateKey(app.slug), created => {
      this.app.set({ ...app, keys: [...app.keys, created.key] });
      this.secrets.show({ title: this.text('app.keyTitle'), message: this.text('app.keyMessage'), value: created.privateKeyPem });
      return this.text('app.keyGenerated');
    });
  }

  deleteKey(app: SparkApp, key: SparkAppKey): Promise<void> {
    if (!confirm(this.text('app.confirmDeleteKey', key.fingerprint))) return Promise.resolve();
    return this.run(this.service.deleteKey(app.slug, key.keyId), updated => {
      this.app.set(updated);
      return this.text('app.keyDeleted');
    });
  }

  async remove(app: SparkApp): Promise<void> {
    if (!confirm(this.text('app.confirmDelete', app.name))) return;
    await this.run(this.service.deleteApp(app.slug), () => this.text('app.deleted'));
    if (!this.error()) await this.router.navigateByUrl(this.links.developerApps());
  }

  private async run<T>(call: Promise<SparkAppsResult<T>>, done: (value: T) => string): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await call;
    if (result.ok) this.notice.set(done(result.value));
    else this.error.set(result.error ?? this.text('failed'));
    this.busy.set(false);
  }
}
