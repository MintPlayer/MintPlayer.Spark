import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG } from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';
import { accountMessages } from './account-messages';

/**
 * Personal data (#460, D8/D16): download everything the account and the application's
 * `ISparkPersonalDataContributor`s hold as JSON, and delete the account.
 *
 * Deletion needs re-authentication: the current password (the field shows only when the account has
 * one, from `account.hasPassword`), or — for an account without one — a sign-in
 * younger than the server's `ReauthenticationMaxAge` (5 minutes). A 403 `reauthentication_required`
 * says which is missing; the page then asks to sign in again. Handlers run first and the account is
 * removed last, so a failure (500 `deletion_failed`) leaves the account intact and retryable.
 */
@Component({
  selector: 'spark-personal-data',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.personalDataTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          <p>{{ 'auth.personalDataDescription' | t }}</p>
          @for (message of downloadMessages(); track message) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block">{{ message | t }}</bs-alert>
          }
          <button type="button" class="btn btn-outline-primary spark-download-data" [disabled]="busy()" (click)="download()">
            {{ 'auth.downloadPersonalData' | t }}
          </button>

          <hr class="my-4" />

          <h5 class="text-danger">{{ 'auth.deleteAccountTitle' | t }}</h5>
          <p>{{ 'auth.deleteAccountDescription' | t }}</p>
          @for (message of deleteMessages(); track message) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-delete-error">{{ message | t }}</bs-alert>
          }
          <bs-form>
            <form [formGroup]="form" (ngSubmit)="deleteAccount()">
              @if (hasPassword() !== false) {
                <div class="mb-3">
                  <label for="deletePassword" class="form-label">{{ 'auth.password' | t }}</label>
                  <input type="password" id="deletePassword" formControlName="password" autocomplete="current-password" />
                  <small class="text-muted">{{ 'auth.deletePasswordHint' | t }}</small>
                </div>
              } @else {
                <p class="text-muted spark-delete-recent-sign-in">{{ 'auth.deleteRecentSignIn' | t }}</p>
              }
              <div class="mb-3">
                <bs-checkbox [type]="'checkbox'" formControlName="confirm" [name]="'confirmDelete'">{{ 'auth.deleteAccountConfirm' | t }}</bs-checkbox>
              </div>
              <button type="submit" class="btn btn-danger spark-delete-account" [disabled]="busy() || !form.controls.confirm.value">
                @if (busy()) { <bs-spinner class="me-1" /> }
                {{ 'auth.deleteAccount' | t }}
              </button>
            </form>
          </bs-form>
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkPersonalDataComponent implements OnInit {
  private readonly auth = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  protected readonly colors = Color;

  readonly form = new FormGroup({
    password: new FormControl('', { nonNullable: true }),
    confirm: new FormControl(false, { nonNullable: true }),
  });

  readonly busy = signal(false);
  readonly downloadMessages = signal<string[]>([]);
  readonly deleteMessages = signal<string[]>([]);
  /**
   * Whether the account has a password (`account.hasPassword` of the personal data). Null while
   * unknown — an older server, or the read failed — which keeps the password field, as before.
   */
  readonly hasPassword = signal<boolean | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      const result = await this.auth.personalData();
      const flag = result.success ? (result.value as { account?: { hasPassword?: unknown } } | null)?.account?.hasPassword : undefined;
      this.hasPassword.set(typeof flag === 'boolean' ? flag : null);
    } catch {
      this.hasPassword.set(null);
    }
  }

  async download(): Promise<void> {
    this.busy.set(true);
    this.downloadMessages.set([]);
    try {
      const result = await this.auth.personalData();
      if (!result.success) {
        this.downloadMessages.set(accountMessages(result));
        return;
      }
      const blob = new Blob([JSON.stringify(result.value, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = 'personal-data.json';
      anchor.click();
      URL.revokeObjectURL(url);
    } finally {
      this.busy.set(false);
    }
  }

  async deleteAccount(): Promise<void> {
    if (!this.form.controls.confirm.value) return;
    this.busy.set(true);
    this.deleteMessages.set([]);
    try {
      const result = await this.auth.deleteAccount(this.form.controls.password.value || null);
      if (result.success) {
        await this.router.navigateByUrl(this.config.defaultRedirectUrl);
        return;
      }
      this.deleteMessages.set(accountMessages(result));
    } finally {
      this.busy.set(false);
    }
  }
}
