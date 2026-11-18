import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, InjectionToken, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SparkAppsService,
  SparkAppsTextPipe,
  injectAppsLinks,
  injectAppsText,
  ownerId,
  type SparkAppInstallPrompt,
  type SparkAppInstallTarget,
} from '@mintplayer/ng-spark/apps/core';

/** Leaves this site for an App's own setup page (the only navigation the router cannot do). */
export const SPARK_APPS_LEAVE_SITE = new InjectionToken<(url: string) => void>('SPARK_APPS_LEAVE_SITE', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    return (url: string) => document.location.assign(url);
  },
});

/**
 * The consent page (§4.3 flow 6, D30): what an App asks to do and to receive, and where to install it, on the
 * user's own account or an organization they administer. After installing, the user goes on to the App's setup
 * URL when it has one (it receives `installation_id`), else to their installations.
 */
@Component({
  selector: 'spark-app-install',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSelectComponent, BsSelectOption, BsSpinnerComponent, SparkAppsTextPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'install.title' | sparkApps: (prompt()?.app?.name ?? '') }}</h3></bs-card-header>
        <div class="p-4">
          @if (error()) { <bs-alert [color]="'danger'" class="mb-3 d-block spark-apps-error">{{ error() }}</bs-alert> }

          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (prompt(); as prompt) {
            @if (prompt.app.description) { <p>{{ prompt.app.description }}</p> }
            @if (prompt.app.homepageUrl) {
              <p><a [href]="prompt.app.homepageUrl" target="_blank" rel="noopener noreferrer">{{ prompt.app.homepageUrl }}</a></p>
            }

            <h6>{{ 'install.permissions' | sparkApps }}</h6>
            <ul class="spark-app-install-permissions">
              @for (permission of prompt.app.permissions; track permission) { <li><code>{{ permission }}</code></li> }
              @empty { <li class="text-muted">{{ 'install.noPermissions' | sparkApps }}</li> }
            </ul>
            <h6>{{ 'install.events' | sparkApps }}</h6>
            <ul class="spark-app-install-events">
              @for (event of prompt.app.events; track event) { <li><code>{{ event }}</code></li> }
              @empty { <li class="text-muted">{{ 'install.noEvents' | sparkApps }}</li> }
            </ul>

            @if (available().length) {
              <div class="mb-3">
                <label class="form-label" for="sparkAppInstallTarget">{{ 'install.target' | sparkApps }}</label>
                <bs-select id="sparkAppInstallTarget" [title]="'install.target' | sparkApps" [ngModel]="target()" (ngModelChange)="target.set($event)">
                  @for (option of available(); track id(option)) {
                    <option [ngValue]="id(option)">{{ option.displayName }}</option>
                  }
                </bs-select>
              </div>
              <button type="button" class="btn btn-primary spark-app-install-confirm" [disabled]="busy() || !target()" (click)="install()">
                @if (busy()) { <bs-spinner class="me-1" /> }
                {{ 'install.confirm' | sparkApps }}
              </button>
            } @else {
              <p class="text-muted spark-app-install-nowhere">{{ 'install.nowhere' | sparkApps }}</p>
            }
            @for (installed of installedAlready(); track id(installed)) {
              <small class="d-block text-muted spark-app-install-already">{{ 'install.already' | sparkApps: installed.displayName }}</small>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAppInstallComponent {
  private readonly service = inject(SparkAppsService);
  private readonly router = inject(Router);
  private readonly leave = inject(SPARK_APPS_LEAVE_SITE);
  private readonly text = injectAppsText();
  private readonly slug = inject(ActivatedRoute).snapshot.paramMap.get('slug') ?? '';
  private readonly links = injectAppsLinks();
  protected readonly id = ownerId;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly prompt = signal<SparkAppInstallPrompt | null>(null);
  readonly target = signal<string | null>(null);
  readonly error = signal('');

  /** Where it is not installed yet. */
  protected readonly available = computed(() => this.prompt()?.targets.filter(t => !t.installationId) ?? []);
  protected readonly installedAlready = computed(() => this.prompt()?.targets.filter(t => !!t.installationId) ?? []);

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    const result = await this.service.installPrompt(this.slug);
    if (result.ok) {
      this.prompt.set(result.value);
      const first = result.value.targets.find(t => !t.installationId);
      this.target.set(first ? ownerId(first) : null);
    } else {
      this.error.set(this.text(result.status === 404 ? 'install.notFound' : 'loadFailed'));
    }
    this.loading.set(false);
  }

  async install(): Promise<void> {
    const target: SparkAppInstallTarget | undefined = this.available().find(t => ownerId(t) === this.target());
    if (!target) return;
    this.busy.set(true);
    this.error.set('');
    const result = await this.service.install(this.slug, target);
    if (!result.ok) {
      this.error.set(result.error ?? this.text('failed'));
      this.busy.set(false);
      return;
    }
    if (result.value.redirectUrl) {
      // The App's own setup page, on its own site: leaving this app is the point.
      this.leave(result.value.redirectUrl);
      return;
    }
    this.busy.set(false);
    await this.router.navigateByUrl(this.links.appInstallations());
  }
}
