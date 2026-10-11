import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsDropdownDividerDirective, BsDropdownHeaderDirective, BsDropdownItemDirective, BsDropdownMenuComponent } from '@mintplayer/ng-bootstrap/dropdown-menu';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { sparkRelativeTime } from './relative-time';
import { SPARK_NOTIFICATIONS_OPTIONS, SparkNotification } from './spark-notification';
import { SparkNotificationsService } from './spark-notifications.service';
import { SPARK_NOTIFICATION_TEXTS, SparkPushToggleComponent } from './spark-push-toggle.component';

/**
 * The notification bell for an app's top bar (`docs/seo_analyzer_PRD.md` X10): the unread count as a
 * badge, live; a menu with the latest notifications (title, message, relative time), "Mark all as read",
 * a link to the full list and, where this browser can receive it, the Web Push opt-in. Choosing a
 * notification marks it read and follows its link.
 */
@Component({
  selector: 'spark-notification-bell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, CdkOverlayOrigin, CdkConnectedOverlay, BsBadgeComponent, BsDropdownMenuComponent, BsDropdownItemDirective,
    BsDropdownHeaderDirective, BsDropdownDividerDirective, SparkIconComponent, SparkPushToggleComponent,
  ],
  template: `
    <button type="button" cdkOverlayOrigin #origin="cdkOverlayOrigin" #toggle
            class="btn btn-link position-relative spark-notification-bell-toggle"
            aria-haspopup="menu" [attr.aria-expanded]="open()" [attr.aria-label]="ariaLabel()"
            (click)="open.set(!open())">
      <spark-icon name="bell" />
      @if (notifications.unreadCount() > 0) {
        <bs-badge [type]="colors.danger" [decorative]="true"
                  class="position-absolute top-0 start-100 translate-middle spark-notification-count">{{ countText() }}</bs-badge>
      }
    </button>
    <ng-template cdkConnectedOverlay
                 [cdkConnectedOverlayOrigin]="origin"
                 [cdkConnectedOverlayOpen]="open()"
                 [cdkConnectedOverlayPositions]="positions"
                 (overlayOutsideClick)="onOutsideClick($event, toggle)"
                 (overlayKeydown)="$event.key === 'Escape' && close()"
                 (detach)="close()">
      <bs-dropdown-menu class="spark-notification-menu">
        <li bsDropdownHeader class="d-flex align-items-center justify-content-between gap-3">
          <span>{{ t('title') }}</span>
          @if (notifications.unreadCount() > 0) {
            <button type="button" class="btn btn-link btn-sm p-0 spark-notification-mark-all" (click)="markAllRead()">{{ t('markAllRead') }}</button>
          }
        </li>
        @for (notification of notifications.recent(); track notification.id) {
          <li bsDropdownItem class="spark-notification" [class.fw-semibold]="!notification.isRead"
              [class.spark-notification-unread]="!notification.isRead" [attr.data-id]="notification.id"
              (click)="choose(notification)">
            <div class="spark-notification-title text-wrap">{{ notification.title }}</div>
            @if (notification.body) {
              <div class="spark-notification-body small fw-normal text-body-secondary text-wrap">{{ notification.body }}</div>
            }
            <div class="spark-notification-time small fw-normal text-body-secondary">{{ when(notification) }}</div>
          </li>
        } @empty {
          <li class="dropdown-item-text text-body-secondary spark-notification-empty">{{ t('empty') }}</li>
        }
        <li bsDropdownDivider></li>
        <li bsDropdownItem class="spark-notification-all">
          <a [routerLink]="listLink()" (click)="close()">{{ t('showAll') }}</a>
        </li>
        @if (notifications.pushConfigured() && notifications.pushState() !== 'unsupported') {
          <li class="dropdown-item-text spark-notification-push"><spark-push-toggle /></li>
        }
      </bs-dropdown-menu>
    </ng-template>
  `,
  styles: [`
    :host { display: inline-block; }
    .spark-notification-count { font-size: .65rem; }
  `],
})
export class SparkNotificationBellComponent {
  protected readonly notifications = inject(SparkNotificationsService);
  private readonly language = inject(SparkLanguageService);
  private readonly options = inject(SPARK_NOTIFICATIONS_OPTIONS);
  protected readonly colors = Color;

  /** Where "All notifications" goes. Default: `provideSparkNotifications({ listLink })`, else the `notifications` query page. */
  readonly allLink = input<string | readonly unknown[] | null>(null);

  readonly open = signal(false);

  protected readonly listLink = computed(() => this.allLink() ?? this.options.listLink);
  protected readonly countText = computed(() => {
    const count = this.notifications.unreadCount();
    return count > 99 ? '99+' : String(count);
  });
  protected readonly ariaLabel = computed(() => {
    const count = this.notifications.unreadCount();
    return count > 0 ? `${this.t('title')} (${count})` : this.t('title');
  });

  readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top' },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom' },
  ];

  protected t(key: string): string {
    return this.language.t('notifications.spa.' + key, SPARK_NOTIFICATION_TEXTS[key]);
  }

  protected when(notification: SparkNotification): string {
    return sparkRelativeTime(notification.createdAt, Date.now(), this.language.language());
  }

  close(): void {
    this.open.set(false);
  }

  /** A click outside closes the menu, except on the bell itself, whose own handler toggles it. */
  onOutsideClick(event: MouseEvent, toggle: HTMLElement): void {
    if (event.target instanceof Node && toggle.contains(event.target)) return;
    this.close();
  }

  async choose(notification: SparkNotification): Promise<void> {
    this.close();
    await this.notifications.open(notification);
  }

  async markAllRead(): Promise<void> {
    await this.notifications.markRead();
  }
}
