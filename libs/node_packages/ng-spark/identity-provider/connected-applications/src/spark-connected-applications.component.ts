import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import {
  SparkConnectedApplication,
  SparkConnectedScope,
  SparkIdentityProviderDatePipe,
  SparkIdentityProviderPortalService,
  SparkIdentityProviderTextPipe,
  injectIdentityProviderText,
} from '@mintplayer/ng-spark/identity-provider/core';

/**
 * The signed-in user's connected applications (`account/applications`, PRD D6/D7): each application
 * the user granted access to, with its logo, publisher, the granted scopes and the dates. The user
 * withdraws the whole grant, or unticks individual scopes and withdraws only those. Required scopes
 * (`openid`, and those the application or resource marks required) go only with the whole grant.
 *
 * The server resolves the scope texts in the request's language; the `spark-lang` cookie
 * `SparkLanguageService` writes is what makes that the language the user picked here.
 */
@Component({
  selector: 'spark-connected-applications',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent,
    BsSpinnerComponent, SparkIconComponent, SparkIdentityProviderTextPipe, SparkIdentityProviderDatePipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 760px;">
        <bs-card-header><h3 class="mb-0">{{ 'apps.title' | idp }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'apps.intro' | idp }}</p>
          @if (notice()) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-idp-notice">{{ notice() }}</bs-alert>
          }
          @if (error()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-idp-error">{{ error() }}</bs-alert>
          }
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else {
            @for (app of apps(); track app.applicationId) {
              <section class="border rounded p-3 mb-3 spark-connected-app">
                <div class="d-flex align-items-center gap-3 mb-2">
                  @if (app.logoUrl) {
                    <img [src]="app.logoUrl" alt="" width="48" height="48" class="rounded spark-connected-app-logo"
                         style="object-fit: contain;" referrerpolicy="no-referrer" />
                  }
                  <div class="text-break">
                    <h5 class="mb-0 spark-connected-app-name">{{ app.displayName }}</h5>
                    @if (app.publisher) {
                      <small class="text-muted spark-connected-app-publisher">{{ 'apps.by' | idp: app.publisher }}</small>
                    }
                    @if (app.homepageUrl) {
                      <a [href]="app.homepageUrl" target="_blank" rel="noopener noreferrer" class="ms-2 small">{{ 'apps.website' | idp }}</a>
                    }
                  </div>
                </div>

                <small class="text-muted d-block spark-connected-app-dates">
                  {{ 'apps.firstGranted' | idp: (app.firstGrantedAt | idpDate) }} ·
                  @if (app.lastUsedAt) { {{ 'apps.lastUsed' | idp: (app.lastUsedAt | idpDate) }} } @else { {{ 'apps.neverUsed' | idp }} }
                </small>
                @if (app.remembered) {
                  <small class="text-muted d-block">
                    @if (app.rememberedUntil) { {{ 'apps.rememberedUntil' | idp: (app.rememberedUntil | idpDate) }} } @else { {{ 'apps.remembered' | idp }} }
                  </small>
                }

                <h6 class="mt-3">{{ 'apps.scopes' | idp }}</h6>
                @if (hasOptionalScopes(app)) {
                  <small class="text-muted d-block mb-2">{{ 'apps.scopesHint' | idp }}</small>
                }
                <bs-form>
                  @for (scope of app.scopes; track scope.name) {
                    <div class="mb-2 spark-connected-scope" [attr.data-scope]="scope.name">
                      @if (scope.required) {
                        <div class="d-flex align-items-start gap-2" [attr.title]="'apps.requiredHint' | idp">
                          <spark-icon name="lock" class="mt-1" />
                          <div>
                            <span>{{ scope.displayName }}</span>
                            <bs-badge [type]="colors.secondary" class="ms-2 spark-scope-required">{{ 'apps.required' | idp }}</bs-badge>
                            @if (scope.description) { <small class="text-muted d-block">{{ scope.description }}</small> }
                          </div>
                        </div>
                      } @else {
                        <bs-checkbox [type]="'checkbox'" [name]="app.applicationId + '|' + scope.name"
                                     [isToggled]="!isUnticked(app, scope)" (isToggledChange)="setTicked(app, scope, $event === true)">
                          {{ scope.displayName }}
                        </bs-checkbox>
                        @if (scope.description) { <small class="text-muted d-block ms-4">{{ scope.description }}</small> }
                      }
                    </div>
                  }
                </bs-form>

                <div class="d-flex flex-wrap gap-2 mt-3">
                  @if (hasOptionalScopes(app)) {
                    <button type="button" class="btn btn-outline-danger btn-sm spark-withdraw-unticked"
                            [disabled]="busy() || untickedOf(app).length === 0" (click)="withdrawUnticked(app)">
                      {{ 'apps.withdrawUnticked' | idp }}
                    </button>
                  }
                  <button type="button" class="btn btn-danger btn-sm spark-withdraw-all" [disabled]="busy()" (click)="withdrawAll(app)">
                    {{ 'apps.withdrawAll' | idp }}
                  </button>
                </div>
              </section>
            } @empty {
              @if (!error()) {
                <p class="text-muted spark-connected-apps-empty">{{ 'apps.none' | idp }}</p>
              }
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkConnectedApplicationsComponent {
  private readonly text = injectIdentityProviderText();
  private readonly portal = inject(SparkIdentityProviderPortalService);
  protected readonly colors = Color;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly apps = signal<SparkConnectedApplication[]>([]);
  readonly error = signal('');
  readonly notice = signal('');
  /** Per application, the scopes the user unticked (to withdraw). */
  private readonly unticked = signal<Record<string, readonly string[]>>({});

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    const result = await this.portal.connectedApplications();
    if (result.ok) {
      this.apps.set(result.value);
      this.unticked.set({});
    } else {
      this.apps.set([]);
      this.error.set(this.text('loadFailed'));
    }
    this.loading.set(false);
  }

  protected hasOptionalScopes(app: SparkConnectedApplication): boolean {
    return app.scopes.some(scope => !scope.required);
  }

  protected isUnticked(app: SparkConnectedApplication, scope: SparkConnectedScope): boolean {
    return this.untickedOf(app).includes(scope.name);
  }

  protected untickedOf(app: SparkConnectedApplication): readonly string[] {
    return this.unticked()[app.applicationId] ?? [];
  }

  protected setTicked(app: SparkConnectedApplication, scope: SparkConnectedScope, ticked: boolean): void {
    if (scope.required) return;
    this.unticked.update(map => {
      const current = map[app.applicationId] ?? [];
      const next = ticked ? current.filter(name => name !== scope.name) : [...new Set([...current, scope.name])];
      return { ...map, [app.applicationId]: next };
    });
  }

  async withdrawUnticked(app: SparkConnectedApplication): Promise<void> {
    // Only optional scopes can be unticked; a required one is never sent on its own.
    const scopes = this.untickedOf(app).filter(name => app.scopes.some(s => s.name === name && !s.required));
    if (!scopes.length) return;
    await this.withdraw(app, scopes);
  }

  async withdrawAll(app: SparkConnectedApplication): Promise<void> {
    if (!confirm(this.text('apps.confirmWithdrawAll', app.displayName))) return;
    await this.withdraw(app, undefined);
  }

  private async withdraw(app: SparkConnectedApplication, scopes: string[] | undefined): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await this.portal.withdraw({ applicationId: app.applicationId, scopes });
    if (result.ok) {
      this.notice.set(this.text('apps.withdrawn'));
      await this.load();
    } else {
      // The server's problem text is English only; the page says the same in the user's language.
      this.error.set(this.text('apps.withdrawFailed'));
    }
    this.busy.set(false);
  }
}
