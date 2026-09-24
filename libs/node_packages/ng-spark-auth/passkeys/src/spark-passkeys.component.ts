import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SparkPasskey, SparkPasskeyError, passkeysSupported } from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

/**
 * Manage the signed-in account's passkeys: enroll, rename, remove.
 *
 * Reachable only by an authenticated user — there is no passkey-first account creation, so this page
 * always operates on an account that already exists.
 */
@Component({
  selector: 'spark-passkeys',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-passkeys.component.html',
})
export class SparkPasskeysComponent {
  private readonly authService = inject(SparkAuthService);

  colors = Color;

  /**
   * Feature detection is read once into a signal rather than called from the template: it cannot
   * change during the page's life, and a function call in a template runs on every check.
   */
  readonly supported = signal(passkeysSupported());

  readonly passkeys = signal<SparkPasskey[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly errorKey = signal('');

  constructor() {
    this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      this.passkeys.set(await this.authService.passkeys());
    } catch {
      this.errorKey.set('auth.passkeyListFailed');
    } finally {
      this.loading.set(false);
    }
  }

  async register(): Promise<void> {
    this.errorKey.set('');
    this.busy.set(true);
    try {
      const result = await this.authService.registerPasskey();
      // A dismissed prompt is not a failure and must not raise a banner — the user chose to stop.
      if (!result.success && result.error !== 'cancelled') {
        this.errorKey.set(this.messageFor(result.error));
        return;
      }
      if (result.success) await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  async rename(passkey: SparkPasskey, name: string): Promise<void> {
    this.errorKey.set('');
    this.busy.set(true);
    try {
      const result = await this.authService.renamePasskey(passkey.id, name);
      if (!result.success) this.errorKey.set(this.messageFor(result.error));
      else await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  async remove(passkey: SparkPasskey): Promise<void> {
    this.errorKey.set('');
    this.busy.set(true);
    try {
      const result = await this.authService.removePasskey(passkey.id);
      if (!result.success) this.errorKey.set(this.messageFor(result.error));
      else await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * `last_credential` gets its own message because the generic one would be actively misleading:
   * the removal did not fail, it was refused to stop the user locking themselves out permanently.
   */
  private messageFor(error?: SparkPasskeyError): string {
    switch (error) {
      case 'unsupported': return 'auth.passkeyUnsupported';
      case 'no_credential': return 'auth.passkeyNoCredential';
      case 'last_credential': return 'auth.passkeyLastCredential';
      default: return 'auth.passkeyFailed';
    }
  }
}
