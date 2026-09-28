import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';
import { accountMessages } from './account-messages';

/**
 * Change the password — or set a first one on an account created through an external provider
 * (`POST /spark/auth/manage/password`, #460 D16). The current password is required only when the
 * account has one; the server says so (`CurrentPasswordRequired`) rather than the page guessing.
 */
@Component({
  selector: 'spark-change-password',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.changePasswordTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          @for (message of messages(); track message) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-account-error">{{ message | t }}</bs-alert>
          }
          @if (saved()) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-account-saved">{{ 'auth.passwordChanged' | t }}</bs-alert>
          }
          <bs-form>
            <form [formGroup]="form" (ngSubmit)="submit()">
              <div class="mb-3">
                <label for="currentPassword" class="form-label">{{ 'auth.currentPassword' | t }}</label>
                <input type="password" id="currentPassword" formControlName="currentPassword" autocomplete="current-password" />
                <small class="text-muted">{{ 'auth.currentPasswordHint' | t }}</small>
              </div>
              <div class="mb-3">
                <label for="newPassword" class="form-label">{{ 'auth.newPassword' | t }}</label>
                <input type="password" id="newPassword" formControlName="newPassword" autocomplete="new-password" />
              </div>
              <div class="mb-3">
                <label for="confirmPassword" class="form-label">{{ 'auth.confirmPassword' | t }}</label>
                <input type="password" id="confirmPassword" formControlName="confirmPassword" autocomplete="new-password" />
              </div>
              <button type="submit" class="btn btn-primary" [disabled]="busy()">
                @if (busy()) { <bs-spinner class="me-1" /> }
                {{ 'auth.savePassword' | t }}
              </button>
            </form>
          </bs-form>
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkChangePasswordComponent {
  private readonly auth = inject(SparkAuthService);
  protected readonly colors = Color;

  readonly form = new FormGroup({
    currentPassword: new FormControl('', { nonNullable: true }),
    newPassword: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    confirmPassword: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly messages = signal<string[]>([]);

  async submit(): Promise<void> {
    this.saved.set(false);
    const { currentPassword, newPassword, confirmPassword } = this.form.getRawValue();
    if (!newPassword) {
      this.messages.set(['auth.newPasswordRequired']);
      return;
    }
    if (newPassword !== confirmPassword) {
      this.messages.set(['auth.passwordMismatch']);
      return;
    }

    this.busy.set(true);
    this.messages.set([]);
    try {
      const result = await this.auth.setPassword(newPassword, currentPassword || null);
      if (result.success) {
        this.saved.set(true);
        this.form.reset();
      } else {
        this.messages.set(accountMessages(result));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
