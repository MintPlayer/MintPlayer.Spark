import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkNotificationsService } from './spark-notifications.service';

/** English fallbacks of `notifications.spa.*`, shown until the server's translations arrive. */
export const SPARK_NOTIFICATION_TEXTS: Record<string, string> = {
  title: 'Notifications',
  empty: 'No notifications',
  markAllRead: 'Mark all as read',
  showAll: 'All notifications',
  pushEnable: 'Turn on push notifications',
  pushDisable: 'Turn off push notifications on this device',
  pushDenied: 'Notifications are blocked for this site in your browser settings.',
  pushUnsupported: 'This browser cannot receive push notifications here. On iPhone and iPad, add the app to your Home Screen first.',
};

/**
 * The Web Push opt-in for this device, on its own (an account or settings page); the notification bell
 * shows the same control. Renders nothing unless the server sends Web Push. Otherwise: a button to turn
 * it on or off, or why it cannot be (blocked in the browser, or a browser without push: on iPhone and
 * iPad the app must be on the Home Screen first).
 */
@Component({
  selector: 'spark-push-toggle',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (notifications.pushConfigured()) {
      @switch (notifications.pushState()) {
        @case ('unsupported') {
          <p class="spark-push-unsupported small text-body-secondary mb-0">{{ t('pushUnsupported') }}</p>
        }
        @case ('denied') {
          <p class="spark-push-denied small text-body-secondary mb-0">{{ t('pushDenied') }}</p>
        }
        @case ('on') {
          <button type="button" class="btn btn-sm btn-outline-secondary spark-push-disable"
                  [disabled]="notifications.pushBusy()" (click)="disable()">{{ t('pushDisable') }}</button>
        }
        @default {
          <button type="button" class="btn btn-sm btn-outline-primary spark-push-enable"
                  [disabled]="notifications.pushBusy()" (click)="enable()">{{ t('pushEnable') }}</button>
        }
      }
    }
  `,
})
export class SparkPushToggleComponent {
  protected readonly notifications = inject(SparkNotificationsService);
  private readonly language = inject(SparkLanguageService);

  protected t(key: string): string {
    return this.language.t('notifications.spa.' + key, SPARK_NOTIFICATION_TEXTS[key]);
  }

  /** Synchronous up to the permission prompt: this is the click handler the prompt needs. */
  protected enable(): void {
    void this.notifications.enablePush();
  }

  protected disable(): void {
    void this.notifications.disablePush();
  }
}
