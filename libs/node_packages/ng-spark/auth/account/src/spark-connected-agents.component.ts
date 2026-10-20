import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import {
  SparkAgentExposedType,
  SparkAgentGrant,
  SparkAgentGrantsOverview,
  SparkAgentGrantType,
  SparkAgentPermission,
  SparkContributedApplication,
  sparkAgentCanWrite,
} from '@mintplayer/ng-spark/auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';
import { accountMessages } from './account-messages';

/** A choice of what an agent may do: read or read + changes, and every exposed type or a chosen list. */
interface GrantChoice {
  changes: boolean;
  /** `null`: everything the application makes available to agents (including types exposed later); otherwise type ids (D28). */
  types: string[] | null;
}

/**
 * Connected applications (MCP PRD D14, mounted by `withConnectedAgents()`): the AI agents the user connected
 * and their personal access tokens, with what each may see and whether it may make changes; edit, disconnect,
 * create a token; then what other packages contribute (sign-ins at other websites).
 *
 * The page offers only what an agent could use: the application's limit for that agent and the types the user
 * may see. A change applies to the agent's next call, without signing in again. A new token is shown once.
 */
@Component({
  selector: 'spark-connected-agents',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent, BsFormControlDirective,
    BsSpinnerComponent, NgTemplateOutlet, TranslateKeyPipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 760px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.agentsTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'auth.agentsDescription' | t }}</p>
          @for (message of messages(); track message) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-agents-error">{{ message | t }}</bs-alert>
          }
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (overview(); as data) {
            @for (grant of data.grants; track grant.key) {
              <section class="border rounded p-3 mb-3 spark-agent-grant" [attr.data-key]="grant.key">
                <div class="d-flex flex-wrap align-items-center gap-2">
                  <h5 class="mb-0 text-break spark-agent-name">{{ grant.name || grant.clientId || grant.key }}</h5>
                  <bs-badge [type]="colors.secondary">{{ 'auth.agentKind.' + grant.kind | t }}</bs-badge>
                  @if (grant.kind === 'ExternalAuthorizationServer') {
                    <bs-badge [type]="colors.info" class="spark-agent-issuer" [title]="'auth.agentIssuer' | t">{{ grant.issuer }}</bs-badge>
                  } @else if (grant.kind !== 'PersonalAccessToken') {
                    @if (grant.verifiedHost) {
                      <bs-badge [type]="colors.success" class="spark-agent-verified">{{ grant.verifiedHost }}</bs-badge>
                    } @else {
                      <bs-badge [type]="colors.warning" class="spark-agent-unverified">{{ 'auth.agentUnverified' | t }}</bs-badge>
                    }
                  }
                  @if (grant.status !== 'active') {
                    <bs-badge [type]="colors.danger" class="spark-agent-status">{{ 'auth.agentStatus.' + grant.status | t }}</bs-badge>
                  }
                </div>
                <small class="text-muted d-block mt-1 spark-agent-dates">
                  {{ 'auth.agentCreated' | t }} {{ date(grant.createdAt) }} ·
                  @if (grant.lastUsedAt) { {{ 'auth.agentLastUsed' | t }} {{ date(grant.lastUsedAt) }} } @else { {{ 'auth.agentNeverUsed' | t }} }
                  @if (grant.expiresAt) { · {{ 'auth.agentExpires' | t }} {{ date(grant.expiresAt) }} }
                </small>

                @if (editing() === grant.key) {
                  <bs-form class="d-block mt-3">
                    @if (canWrite(grant.maxPermissions)) {
                      <bs-checkbox [type]="'checkbox'" [name]="'changes-' + grant.key" [isToggled]="draft().changes"
                                   (isToggledChange)="setChanges($event === true)">{{ 'auth.agentAllowChanges' | t }}</bs-checkbox>
                    }
                    <ng-container *ngTemplateOutlet="typePicker" />
                  </bs-form>
                  <div class="d-flex gap-2 mt-3">
                    <button type="button" class="btn btn-primary btn-sm spark-agent-save" [disabled]="busy() || !draftValid()" (click)="save(grant)">{{ 'auth.agentSave' | t }}</button>
                    <button type="button" class="btn btn-outline-secondary btn-sm" [disabled]="busy()" (click)="editing.set(null)">{{ 'auth.agentCancel' | t }}</button>
                  </div>
                } @else {
                  <div class="mt-2 spark-agent-scope">
                    <span>{{ (canWrite(grant.permissions) ? 'auth.agentReadAndChanges' : 'auth.agentReadOnly') | t }}</span> ·
                    @if (grant.types === null) {
                      <span>{{ 'auth.agentEverything' | t }}</span>
                    } @else {
                      <span>{{ typeLabels(grant.types) }}</span>
                    }
                  </div>
                  <div class="d-flex flex-wrap gap-2 mt-3">
                    <!-- A disconnected external agent (by the user or a security-stamp change) is allowed again by an edit. -->
                    @if (grant.status === 'active' || grant.status === 'disconnected') {
                      <button type="button" class="btn btn-outline-primary btn-sm spark-agent-edit" [disabled]="busy()" (click)="edit(grant)">{{ 'auth.agentEdit' | t }}</button>
                    }
                    @if (grant.status !== 'disconnected') {
                      <button type="button" class="btn btn-danger btn-sm spark-agent-revoke" [disabled]="busy()" (click)="revoke(grant)">
                        {{ (grant.kind === 'PersonalAccessToken' ? 'auth.agentRevokeToken' : 'auth.agentDisconnect') | t }}
                      </button>
                    }
                  </div>
                }
              </section>
            } @empty {
              <p class="text-muted spark-agents-empty">{{ 'auth.agentsNone' | t }}</p>
            }

            @if (data.personalAccessTokens) {
              <hr class="my-4" />
              <h5>{{ 'auth.agentTokenTitle' | t }}</h5>
              <p class="text-muted">{{ 'auth.agentTokenDescription' | t }}</p>
              @if (createdToken(); as token) {
                <bs-alert [type]="colors.warning" class="mb-3 d-block spark-agent-token-created">
                  <strong>{{ 'auth.agentTokenShownOnce' | t }}</strong>
                  <code class="d-block text-break my-2 spark-agent-token">{{ token }}</code>
                  <button type="button" class="btn btn-sm btn-outline-dark" (click)="copy(token)">{{ 'auth.agentTokenCopy' | t }}</button>
                </bs-alert>
              }
              @if (creating()) {
                <bs-form class="d-block">
                  <div class="mb-3">
                    <label for="sparkAgentTokenName" class="form-label">{{ 'auth.agentTokenName' | t }}</label>
                    <input id="sparkAgentTokenName" type="text" maxlength="100" [value]="tokenName()" (input)="tokenName.set(value($event))" />
                  </div>
                  <div class="mb-3">
                    <label for="sparkAgentTokenDays" class="form-label">{{ 'auth.agentTokenDays' | t }}</label>
                    <input id="sparkAgentTokenDays" type="number" min="1" [max]="data.maxTokenLifetimeDays" [value]="tokenDays()" (input)="tokenDays.set(+value($event))" />
                  </div>
                  @if (canWrite(data.tokenPermissions)) {
                    <bs-checkbox [type]="'checkbox'" [name]="'changes-new'" [isToggled]="draft().changes"
                                 (isToggledChange)="setChanges($event === true)">{{ 'auth.agentAllowChanges' | t }}</bs-checkbox>
                  }
                  <ng-container *ngTemplateOutlet="typePicker" />
                </bs-form>
                <div class="d-flex gap-2 mt-3">
                  <button type="button" class="btn btn-primary spark-agent-token-create" [disabled]="busy() || !tokenValid()" (click)="createToken()">{{ 'auth.agentTokenCreate' | t }}</button>
                  <button type="button" class="btn btn-outline-secondary" [disabled]="busy()" (click)="creating.set(false)">{{ 'auth.agentCancel' | t }}</button>
                </div>
              } @else {
                <button type="button" class="btn btn-outline-primary spark-agent-token-new" [disabled]="busy()" (click)="startToken()">{{ 'auth.agentTokenNew' | t }}</button>
              }
            }

            @if (data.applications.length) {
              <hr class="my-4" />
              <h5>{{ 'auth.agentApplicationsTitle' | t }}</h5>
              @for (app of data.applications; track app.source + '|' + app.id) {
                <section class="border rounded p-3 mb-2 d-flex justify-content-between align-items-start gap-2 spark-contributed-app">
                  <div class="text-break">
                    <strong>{{ app.name }}</strong>
                    @if (app.description) { <small class="text-muted d-block">{{ app.description }}</small> }
                    @if (app.lastUsedAt) { <small class="text-muted d-block">{{ 'auth.agentLastUsed' | t }} {{ date(app.lastUsedAt) }}</small> }
                  </div>
                  <button type="button" class="btn btn-danger btn-sm" [disabled]="busy()" (click)="revokeApplication(app)">{{ 'auth.agentDisconnect' | t }}</button>
                </section>
              }
            }
          }

          <ng-template #typePicker>
            <div class="mt-2">
              <bs-checkbox [type]="'checkbox'" [name]="'everything'" [isToggled]="draft().types === null"
                           (isToggledChange)="setEverything($event === true)">{{ 'auth.agentEverything' | t }}</bs-checkbox>
              @if (draft().types !== null) {
                @for (type of overview()?.types ?? []; track type.id) {
                  <div class="ms-4 spark-agent-type" [attr.data-type]="type.name">
                    <bs-checkbox [type]="'checkbox'" [name]="'type-' + type.name" [isToggled]="draft().types!.includes(type.id)"
                                 (isToggledChange)="setType(type, $event === true)">{{ type.label }}</bs-checkbox>
                    @if (type.queries.length) { <small class="text-muted d-block ms-4">{{ type.queries.join(', ') }}</small> }
                  </div>
                } @empty {
                  <small class="text-muted d-block ms-4">{{ 'auth.agentNoTypes' | t }}</small>
                }
              }
            </div>
          </ng-template>
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkConnectedAgentsComponent implements OnInit {
  private readonly auth = inject(SparkAuthService);
  private readonly translation = inject(SparkAuthTranslationService);
  protected readonly colors = Color;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly overview = signal<SparkAgentGrantsOverview | null>(null);
  readonly messages = signal<string[]>([]);
  /** The key of the grant being edited. */
  readonly editing = signal<string | null>(null);
  readonly creating = signal(false);
  readonly draft = signal<GrantChoice>({ changes: false, types: null });
  readonly tokenName = signal('');
  readonly tokenDays = signal(90);
  /** The token just created: shown until the next change on the page, and never again. */
  readonly createdToken = signal<string | null>(null);

  readonly draftValid = computed(() => this.draft().types === null || this.draft().types!.length > 0);
  readonly tokenValid = computed(() => {
    const max = this.overview()?.maxTokenLifetimeDays ?? 365;
    const days = this.tokenDays();
    return this.draftValid() && this.tokenName().trim().length > 0 && Number.isInteger(days) && days >= 1 && days <= max;
  });

  protected readonly canWrite = sparkAgentCanWrite;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    const result = await this.auth.agentGrants();
    if (result.success && result.value) {
      this.overview.set(result.value);
    } else {
      this.overview.set(null);
      this.messages.set(accountMessages(result));
    }
    this.loading.set(false);
  }

  edit(grant: SparkAgentGrant): void {
    this.creating.set(false);
    this.messages.set([]);
    this.draft.set({ changes: sparkAgentCanWrite(grant.permissions), types: grant.types?.map(t => t.typeId) ?? null });
    this.editing.set(grant.key);
  }

  startToken(): void {
    this.editing.set(null);
    this.messages.set([]);
    this.createdToken.set(null);
    this.tokenName.set('');
    this.tokenDays.set(this.overview()?.defaultTokenLifetimeDays ?? 90);
    this.draft.set({ changes: false, types: null });
    this.creating.set(true);
  }

  setChanges(changes: boolean): void {
    this.draft.update(d => ({ ...d, changes }));
  }

  setEverything(everything: boolean): void {
    this.draft.update(d => ({ ...d, types: everything ? null : [] }));
  }

  setType(type: SparkAgentExposedType, chosen: boolean): void {
    this.draft.update(d => {
      const types = (d.types ?? []).filter(id => id !== type.id);
      return { ...d, types: chosen ? [...types, type.id] : types };
    });
  }

  async save(grant: SparkAgentGrant): Promise<void> {
    const choice = this.draft();
    await this.run(() => this.auth.updateAgentGrant({ key: grant.key, permissions: this.permission(choice), types: this.types(choice) }),
      () => this.editing.set(null));
  }

  async createToken(): Promise<void> {
    const choice = this.draft();
    this.busy.set(true);
    this.messages.set([]);
    try {
      const result = await this.auth.createAgentToken({
        name: this.tokenName().trim(),
        expiresInDays: this.tokenDays(),
        permissions: this.permission(choice),
        types: this.types(choice),
      });
      if (!result.success || !result.value) {
        this.messages.set(accountMessages(result));
        return;
      }
      this.creating.set(false);
      await this.load();
      this.createdToken.set(result.value.token);
    } finally {
      this.busy.set(false);
    }
  }

  async revoke(grant: SparkAgentGrant): Promise<void> {
    if (!confirm(this.translation.t('auth.agentConfirmRevoke'))) return;
    await this.run(() => this.auth.revokeAgentGrant(grant.key));
  }

  async revokeApplication(app: SparkContributedApplication): Promise<void> {
    if (!confirm(this.translation.t('auth.agentConfirmRevoke'))) return;
    await this.run(() => this.auth.revokeConnectedApplication(app.source, app.id));
  }

  protected typeLabels(types: SparkAgentGrantType[]): string {
    const labels = new Map((this.overview()?.types ?? []).map(t => [t.id, t.label] as const));
    return types.map(t => labels.get(t.typeId) ?? t.typeId).join(', ');
  }

  protected date(iso: string | null): string {
    return iso ? new Date(iso).toLocaleDateString() : '';
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected copy(token: string): void {
    void navigator.clipboard?.writeText(token);
  }

  private permission(choice: GrantChoice): SparkAgentPermission {
    return choice.changes ? 'Read, Write' : 'Read';
  }

  /** Each chosen type gets what the grant gets, within what the type itself allows. */
  private types(choice: GrantChoice): SparkAgentGrantType[] | null {
    if (choice.types === null) return null;
    const exposed = new Map((this.overview()?.types ?? []).map(t => [t.id, t] as const));
    return choice.types.map(typeId => ({
      typeId,
      permissions: choice.changes && sparkAgentCanWrite(exposed.get(typeId)?.permissions) ? 'Read, Write' : 'Read',
    }));
  }

  private async run(call: () => Promise<{ success: boolean }>, after?: () => void): Promise<void> {
    this.busy.set(true);
    this.messages.set([]);
    try {
      const result = await call();
      if (!result.success) {
        this.messages.set(accountMessages(result as never));
        return;
      }
      after?.();
      this.createdToken.set(null);
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }
}
