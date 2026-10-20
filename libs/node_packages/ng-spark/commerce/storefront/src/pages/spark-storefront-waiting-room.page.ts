import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Router } from '@angular/router';
import { SparkWaitingRoomComponent } from '@mintplayer/ng-spark/waiting-room';
import { inAppPath, isInAppUrl } from '../storefront-paths';
import { SparkStorefrontAdmissions } from '../storefront-admissions';

/**
 * The waiting room page (`/queue/{key}?scope=&return=`, M16): the queue with its countdown and presence check. Once
 * admitted, the single-use token is kept for this tab under `scope` (the phase's `ticketing-phase:{id}`) and the buyer
 * goes back to `return`, or to the room's own admitted URL. Only in-app URLs are followed (no open redirect).
 */
@Component({
  selector: 'spark-storefront-waiting-room-page',
  imports: [SparkWaitingRoomComponent],
  template: `<spark-waiting-room [key]="key()" (admitted)="admitted($event)" />`,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontWaitingRoomPage {
  private readonly router = inject(Router);
  private readonly admissions = inject(SparkStorefrontAdmissions);

  /** The `:key` route value. */
  key = input.required<string>();

  /** `?scope=`: the admission scope the token is for. */
  scope = input<string | null | undefined>(null);

  /** `?return=`: the in-app page to go back to. */
  returnUrl = input<string | null | undefined>(null, { alias: 'return' });

  protected admitted(admission: { token: string; url: string | null }): void {
    const scope = this.scope();
    if (scope) this.admissions.set(scope, admission.token);
    const origin = typeof location === 'undefined' ? 'http://localhost' : location.origin;
    const target = [this.returnUrl(), admission.url].find((u) => isInAppUrl(u, origin));
    if (target) void this.router.navigateByUrl(inAppPath(target, origin));
  }
}
