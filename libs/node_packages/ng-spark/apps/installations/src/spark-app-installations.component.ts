import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SparkAppsDatePipe,
  SparkAppsOwnerPickerComponent,
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsText,
  type SparkAppInstallation,
  type SparkAppsResult,
  type SparkWebhookOwner,
} from '@mintplayer/ng-spark/apps/core';

/**
 * The Apps installed on the user's account and on the organizations they administer (D30): what each may do and
 * receives, the permissions it asks for since (accept them to grant them), suspend (its tokens stop working and it
 * receives nothing) and uninstall.
 */
@Component({
  selector: 'spark-app-installations',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent,
    SparkAppsOwnerPickerComponent, SparkAppsTextPipe, SparkAppsDatePipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ 'installations.title' | sparkApps }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'installations.intro' | sparkApps }}</p>
          @if (notice()) { <bs-alert [color]="'success'" class="mb-3 d-block spark-apps-notice">{{ notice() }}</bs-alert> }
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          <spark-apps-owner-picker [owners]="owners()" [selected]="owner()" (selectedChange)="choose($event)" />

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else {
            @for (installation of installations(); track installation.id) {
              <div class="border rounded p-3 mb-3 spark-app-installation" [attr.data-id]="installation.id">
                <div class="d-flex justify-content-between">
                  <span class="fw-semibold">{{ installation.appName }}</span>
                  @if (installation.suspended) {
                    <bs-badge [color]="'warning'" class="spark-installation-state" data-state="suspended">{{ 'installations.suspended' | sparkApps }}</bs-badge>
                  }
                </div>
                <small class="d-block text-muted">{{ 'installations.installedAt' | sparkApps: (installation.installedUtc | sparkAppsDate) }}</small>
                <small class="d-block">{{ 'installations.permissions' | sparkApps }} {{ installation.permissions.join(', ') || '—' }}</small>
                <small class="d-block mb-2">{{ 'installations.events' | sparkApps }} {{ installation.events.join(', ') || '—' }}</small>
                @if (installation.pendingPermissions.length) {
                  <bs-alert [color]="'info'" class="mb-2 d-block spark-installation-pending">
                    {{ 'installations.asks' | sparkApps: installation.pendingPermissions.join(', ') }}
                    <button type="button" class="btn btn-sm btn-primary ms-2 spark-installation-accept" [disabled]="busy()" (click)="accept(installation)">{{ 'installations.accept' | sparkApps }}</button>
                  </bs-alert>
                }
                <div class="d-flex flex-wrap gap-2">
                  @if (installation.suspended) {
                    <button type="button" class="btn btn-sm btn-outline-success spark-installation-unsuspend" [disabled]="busy()" (click)="suspend(installation, false)">{{ 'installations.unsuspend' | sparkApps }}</button>
                  } @else {
                    <button type="button" class="btn btn-sm btn-outline-warning spark-installation-suspend" [disabled]="busy()" (click)="suspend(installation, true)">{{ 'installations.suspend' | sparkApps }}</button>
                  }
                  <button type="button" class="btn btn-sm btn-outline-danger spark-installation-uninstall" [disabled]="busy()" (click)="uninstall(installation)">{{ 'installations.uninstall' | sparkApps }}</button>
                </div>
              </div>
            } @empty {
              <p class="text-muted spark-app-installations-empty">{{ 'installations.none' | sparkApps }}</p>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAppInstallationsComponent {
  private readonly service = inject(SparkAppsService);
  private readonly text = injectAppsText();

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly owners = signal<SparkWebhookOwner[]>([]);
  readonly owner = signal<SparkWebhookOwner | null>(null);
  readonly installations = signal<SparkAppInstallation[]>([]);
  readonly error = signal('');
  readonly notice = signal('');

  constructor() {
    void this.init();
  }

  private async init(): Promise<void> {
    const owners = await this.service.owners();
    if (!owners.ok) {
      this.error.set(this.text('loadFailed'));
      this.loading.set(false);
      return;
    }
    this.owners.set(owners.value);
    await this.choose(owners.value[0] ?? null);
  }

  async choose(owner: SparkWebhookOwner | null): Promise<void> {
    this.owner.set(owner);
    await this.reload();
  }

  private async reload(): Promise<void> {
    const owner = this.owner();
    if (!owner) {
      this.installations.set([]);
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    const result = await this.service.installations(owner);
    if (result.ok) this.installations.set(result.value);
    else this.error.set(this.text('loadFailed'));
    this.loading.set(false);
  }

  accept(installation: SparkAppInstallation): Promise<void> {
    return this.run(this.service.acceptPermissions(installation.id), () => this.text('installations.accepted'));
  }

  suspend(installation: SparkAppInstallation, suspended: boolean): Promise<void> {
    return this.run(this.service.setSuspended(installation.id, suspended),
      () => this.text(suspended ? 'installations.suspendedNotice' : 'installations.unsuspendedNotice'));
  }

  uninstall(installation: SparkAppInstallation): Promise<void> {
    if (!confirm(this.text('installations.confirmUninstall', installation.appName))) return Promise.resolve();
    return this.run(this.service.uninstall(installation.id), () => this.text('installations.uninstalled'));
  }

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
