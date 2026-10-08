import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsSelectComponent, BsSelectOption, BsSelectValueAccessor } from '@mintplayer/ng-bootstrap/select';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import {
  SPARK_ACCOUNT_PROFILE_FIELDS,
  SparkAccountProfile,
  SparkAccountProfileField,
  SparkAccountResult,
} from '@mintplayer/ng-spark/auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';
import { accountMessages, fieldMessages } from './account-messages';

interface CultureOption {
  value: string;
  label: string;
}

/**
 * The profile page (#460, D16): user name, email (changeable only under the server's opt-in
 * `SparkEmailChange.Enabled`; a change is confirmed from the NEW address first),
 * the preferred mail language (`SparkUser.PreferredCulture`, M8) and the application's own fields
 * (`SPARK_ACCOUNT_PROFILE_FIELDS`, validated server-side by `ISparkProfileContributor<TUser>`).
 *
 * The language list is the application's UI languages (`GET /spark/culture`); a stored culture not in
 * that list (e.g. `nl-BE`) is kept as an option so opening the page never changes it.
 */
@Component({
  selector: 'spark-account-profile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsInputGroupComponent, BsSelectComponent, BsSelectValueAccessor, BsSelectOption, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.profileTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else {
            @for (message of messages(); track message) {
              <bs-alert [type]="colors.danger" class="mb-3 d-block spark-account-error">{{ message | t }}</bs-alert>
            }
            @if (saved()) {
              <bs-alert [type]="colors.success" class="mb-3 d-block spark-account-saved">{{ 'auth.profileSaved' | t }}</bs-alert>
            }

            <bs-form>
              <form [formGroup]="form" (ngSubmit)="save()">
                <div class="mb-3">
                  <label for="userName" class="form-label">{{ 'auth.userName' | t }}</label>
                  <input type="text" id="userName" formControlName="userName" autocomplete="username" />
                  @for (message of userNameErrors(); track message) {
                    <div class="text-danger small">{{ message | t }}</div>
                  }
                </div>

                <div class="mb-3">
                  <label for="preferredCulture" class="form-label">{{ 'auth.preferredCulture' | t }}</label>
                  <bs-select id="preferredCulture" formControlName="preferredCulture" class="w-100">
                    <option [ngValue]="''">{{ 'auth.preferredCultureDefault' | t }}</option>
                    @for (culture of cultureOptions(); track culture.value) {
                      <option [ngValue]="culture.value">{{ culture.label }}</option>
                    }
                  </bs-select>
                  <small class="text-muted">{{ 'auth.preferredCultureHint' | t }}</small>
                  @for (message of fieldErrors('PreferredCulture'); track message) {
                    <div class="text-danger small">{{ message | t }}</div>
                  }
                </div>

                <div formGroupName="fields">
                  @for (field of profileFields; track field.name) {
                    <div class="mb-3 spark-profile-field">
                      @if (field.type === 'checkbox') {
                        <bs-checkbox [type]="'checkbox'" [formControlName]="field.name" [name]="field.name">{{ field.label | t }}</bs-checkbox>
                      } @else {
                        <label [for]="'field-' + field.name" class="form-label">{{ field.label | t }}</label>
                        @switch (field.type) {
                          @case ('textarea') {
                            <textarea [id]="'field-' + field.name" [formControlName]="field.name" rows="3"
                                      [attr.maxlength]="field.maxLength ?? null" [required]="!!field.required"></textarea>
                          }
                          @case ('select') {
                            <bs-select [id]="'field-' + field.name" [formControlName]="field.name" class="w-100">
                              @if (!field.required) {
                                <option [ngValue]="null"></option>
                              }
                              @for (option of field.options ?? []; track option.value) {
                                <option [ngValue]="option.value">{{ option.label | t }}</option>
                              }
                            </bs-select>
                          }
                          @default {
                            <input [type]="field.type ?? 'text'" [id]="'field-' + field.name" [formControlName]="field.name"
                                   [attr.maxlength]="field.maxLength ?? null" [required]="!!field.required" />
                          }
                        }
                      }
                      @if (field.hint) {
                        <small class="text-muted">{{ field.hint | t }}</small>
                      }
                      @for (message of fieldErrors(field.name); track message) {
                        <div class="text-danger small">{{ message | t }}</div>
                      }
                    </div>
                  }
                </div>

                <button type="submit" class="btn btn-primary" [disabled]="busy()">
                  @if (busy()) { <bs-spinner class="me-1" /> }
                  {{ 'auth.saveProfile' | t }}
                </button>
              </form>
            </bs-form>

            <hr class="my-4" />

            <h5>{{ 'auth.email' | t }}</h5>
            <p class="spark-current-email">
              {{ email() }}
              @if (emailConfirmed() === false) {
                <span class="text-warning ms-2">{{ 'auth.emailNotConfirmed' | t }}</span>
              }
            </p>
            @if (emailMessages().length) {
              @for (message of emailMessages(); track message) {
                <bs-alert [type]="colors.danger" class="mb-3 d-block">{{ message | t }}</bs-alert>
              }
            }
            @if (emailSent()) {
              <bs-alert [type]="colors.info" class="mb-3 d-block spark-email-sent">{{ 'auth.emailChangeSent' | t }}</bs-alert>
            }
            <!-- Opt-in on the server (SparkEmailChange.Enabled); hidden until it says so. -->
            @if (emailChangeAllowed()) {
              <bs-form>
                <form [formGroup]="emailForm" (ngSubmit)="changeEmail()">
                  <label for="newEmail" class="form-label">{{ 'auth.newEmail' | t }}</label>
                  <bs-input-group>
                    <input type="email" id="newEmail" formControlName="newEmail" autocomplete="email" />
                    <button type="submit" class="btn btn-outline-primary" [disabled]="busy()">{{ 'auth.changeEmail' | t }}</button>
                  </bs-input-group>
                </form>
              </bs-form>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAccountProfileComponent {
  private readonly auth = inject(SparkAuthService);
  private readonly http = inject(HttpClient);
  protected readonly colors = Color;

  /** The app's fields, ordered; each becomes a control under `fields`. */
  protected readonly profileFields: SparkAccountProfileField[] = [...(inject(SPARK_ACCOUNT_PROFILE_FIELDS, { optional: true }) ?? [])]
    .map((field, index) => ({ field, index }))
    .sort((a, b) => (a.field.order ?? Infinity) - (b.field.order ?? Infinity) || a.index - b.index)
    .map(x => x.field);

  readonly form = new FormGroup({
    userName: new FormControl('', { nonNullable: true }),
    preferredCulture: new FormControl<string>('', { nonNullable: true }),
    fields: new FormGroup<Record<string, FormControl<unknown>>>({}),
  });
  readonly emailForm = new FormGroup({ newEmail: new FormControl('', { nonNullable: true }) });

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly messages = signal<string[]>([]);
  private readonly lastResult = signal<SparkAccountResult<unknown> | null>(null);

  readonly email = signal<string | null>(null);
  readonly emailConfirmed = signal<boolean | null>(null);
  readonly emailSent = signal(false);
  readonly emailMessages = signal<string[]>([]);
  /** Off until the server reports `emailChange`, and off if the capabilities call fails. */
  readonly emailChangeAllowed = signal(false);

  private readonly languages = signal<CultureOption[]>([]);
  private readonly storedCulture = signal<string | null>(null);

  readonly cultureOptions = computed(() => {
    const options = this.languages();
    const stored = this.storedCulture();
    return stored && !options.some(o => o.value.toLowerCase() === stored.toLowerCase())
      ? [...options, { value: stored, label: stored }]
      : options;
  });

  readonly userNameErrors = computed(() => {
    const result = this.lastResult();
    if (!result?.errors) return [];
    return Object.entries(result.errors)
      .filter(([key]) => key.toLowerCase().includes('username'))
      .flatMap(([, messages]) => messages);
  });

  constructor() {
    for (const field of this.profileFields) {
      this.form.controls.fields.addControl(field.name, new FormControl<unknown>(field.type === 'checkbox' ? false : null));
    }
    void this.load();
  }

  private isInlineError(key: string): boolean {
    const lowered = key.toLowerCase();
    return lowered === 'preferredculture'
      || lowered.includes('username')
      || this.profileFields.some(f => f.name.toLowerCase() === lowered);
  }

  protected fieldErrors(name: string): string[] {
    return fieldMessages(this.lastResult(), name);
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    const [profile, info] = await Promise.all([
      this.auth.profile(),
      this.auth.accountInfo(),
      this.loadLanguages(),
      this.auth.capabilities()
        .then(capabilities => this.emailChangeAllowed.set(capabilities.emailChange === true))
        .catch(() => { /* stays false */ }),
    ]);
    if (profile.success && profile.value) {
      this.apply(profile.value);
    } else {
      this.messages.set(accountMessages(profile));
    }
    if (info.success && info.value) {
      this.email.set(info.value.email);
      this.emailConfirmed.set(info.value.isEmailConfirmed);
    } else if (profile.value) {
      this.email.set(profile.value.email);
    }
    this.loading.set(false);
  }

  private async loadLanguages(): Promise<void> {
    try {
      const culture = await firstValueFrom(this.http.get<{ languages: Record<string, Record<string, string>> }>('/spark/culture'));
      const lang = (globalThis as { __sparkCurrentLanguage?: () => string }).__sparkCurrentLanguage?.() ?? 'en';
      this.languages.set(Object.entries(culture.languages ?? {}).map(([value, label]) => ({
        value,
        label: label?.[lang] ?? label?.['en'] ?? Object.values(label ?? {})[0] ?? value,
      })));
    } catch {
      // No culture endpoint: only "default" and the stored value are offered.
    }
  }

  private apply(profile: SparkAccountProfile): void {
    this.storedCulture.set(profile.preferredCulture);
    this.form.controls.userName.setValue(profile.userName ?? '');
    this.form.controls.preferredCulture.setValue(profile.preferredCulture ?? '');
    for (const field of this.profileFields) {
      const key = Object.keys(profile.fields ?? {}).find(k => k.toLowerCase() === field.name.toLowerCase());
      const value = key ? profile.fields[key] : null;
      this.form.controls.fields.controls[field.name]?.setValue(field.type === 'checkbox' ? value === true : value ?? null);
    }
  }

  async save(): Promise<void> {
    this.saved.set(false);
    this.busy.set(true);
    this.messages.set([]);
    this.lastResult.set(null);
    try {
      const value = this.form.getRawValue();
      const result = await this.auth.updateProfile({
        userName: value.userName.trim() || null,
        preferredCulture: value.preferredCulture || null,
        ...(this.profileFields.length ? { fields: { ...value.fields } } : {}),
      });
      this.lastResult.set(result);
      if (result.success && result.value) {
        this.apply(result.value);
        this.saved.set(true);
      } else {
        // Field errors render next to their control; only the rest goes to the banner.
        this.messages.set(result.errors
          ? Object.entries(result.errors)
              .filter(([key]) => !this.isInlineError(key))
              .flatMap(([, messages]) => messages)
          : accountMessages(result));
      }
    } finally {
      this.busy.set(false);
    }
  }

  async changeEmail(): Promise<void> {
    const newEmail = this.emailForm.controls.newEmail.value.trim();
    this.emailSent.set(false);
    this.emailMessages.set([]);
    if (!newEmail) return;

    this.busy.set(true);
    try {
      const result = await this.auth.changeEmail(newEmail);
      if (result.success) {
        this.emailSent.set(true);
        this.emailForm.reset();
      } else {
        this.emailMessages.set(accountMessages(result));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
