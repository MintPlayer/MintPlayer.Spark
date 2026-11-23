import { isPlatformBrowser } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, PLATFORM_ID, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';
import { RouterLink } from '@angular/router';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent } from '@mintplayer/ng-bootstrap/card';
import { BsDropdownDividerComponent, BsDropdownHeaderComponent, BsDropdownItemComponent, BsDropdownMenuComponent } from '@mintplayer/ng-bootstrap/dropdown';
import { NotificationKind } from '@mintplayer/ng-spark/client-operations';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_NOTIFICATIONS_OPTIONS, SparkBellEntry } from './spark-notification';
import { SparkNotificationListComponent } from './spark-notification-list.component';
import { SparkNotificationsService } from './spark-notifications.service';
import { SPARK_NOTIFICATION_TEXTS, SparkPushToggleComponent } from './spark-push-toggle.component';

/** How long the pop-out shows an entry that names no duration. */
export const SPARK_POPOUT_DEFAULT_MS = 6000;
/** At most this many entries pop out at once; an older one makes room. */
export const SPARK_POPOUT_MAX = 3;
const SHOWN_KEY = 'spark.notifications.shown';
const SHOWN_MAX = 100;

/**
 * The notification bell for an app's top bar, and the one place Spark shows notifications (#710: one UI system).
 *
 * - **The badge** counts what is unread: stored notifications (`MintPlayer.Spark.Notifications`, live over
 *   `/spark/live`) and this session's `notify` client operations.
 * - **The menu** lists both, newest first ({@link SparkNotificationListComponent}), with "Mark all as read", a link to
 *   the full list and, where this browser can receive it, the Web Push opt-in. Opening it marks the session entries
 *   read. Choosing a stored notification marks it read and follows its link.
 * - **The pop-out** shows what just arrived below the bell, at most {@link SPARK_POPOUT_MAX}, each for its own duration
 *   or {@link SPARK_POPOUT_DEFAULT_MS}; an error, or a duration of 0, stays until closed, and hovering pauses them.
 *   Nothing pops out while the menu is open, for what a page load finds, on the server, or twice for one stored
 *   notification in this browser.
 *
 * Without `withNotifications()` (no server package) the bell lists the session entries only.
 */
@Component({
  selector: 'spark-notification-bell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, CdkOverlayOrigin, CdkConnectedOverlay, BsBadgeComponent, BsCardComponent, BsDropdownMenuComponent, BsDropdownItemComponent,
    BsDropdownHeaderComponent, BsDropdownDividerComponent, SparkIconComponent, SparkPushToggleComponent, SparkNotificationListComponent,
  ],
  template: `
    <button type="button" cdkOverlayOrigin #origin="cdkOverlayOrigin" #toggle
            class="btn btn-link position-relative spark-notification-bell-toggle"
            aria-haspopup="menu" [attr.aria-expanded]="open()" [attr.aria-label]="ariaLabel()"
            (click)="setOpen(!open())">
      <spark-icon name="bell" />
      @if (notifications.unreadCount() > 0) {
        <bs-badge color="danger" [decorative]="true"
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
        <bs-dropdown-header class="d-flex align-items-center justify-content-between gap-3">
          <span>{{ t('title') }}</span>
          @if (notifications.serverUnreadCount() > 0) {
            <button type="button" class="btn btn-link btn-sm p-0 spark-notification-mark-all" (click)="markAllRead()">{{ t('markAllRead') }}</button>
          }
        </bs-dropdown-header>
        <spark-notification-list class="spark-notification-menu-list" [entries]="notifications.entries()"
                                 (choose)="choose($event)" (dismiss)="notifications.dismiss($event)" />
        @if (notifications.server) {
          <bs-dropdown-divider />
          <bs-dropdown-item class="spark-notification-all">
            <a [routerLink]="listLink()" (click)="close()">{{ t('showAll') }}</a>
          </bs-dropdown-item>
        }
        @if (notifications.pushConfigured() && notifications.pushState() !== 'unsupported') {
          <div class="dropdown-item-text spark-notification-push"><spark-push-toggle /></div>
        }
      </bs-dropdown-menu>
    </ng-template>
    <ng-template cdkConnectedOverlay
                 [cdkConnectedOverlayOrigin]="origin"
                 [cdkConnectedOverlayOpen]="popped().length > 0 && !open()"
                 [cdkConnectedOverlayPositions]="positions">
      <bs-card class="spark-notification-popout shadow" role="status" aria-live="polite" [attr.aria-label]="t('arrived')"
               (mouseenter)="pause()" (mouseleave)="resume()" (focusin)="pause()" (focusout)="resume()">
        <spark-notification-list [entries]="popped()" [dismissible]="true"
                                 (choose)="chooseFromPopout($event)" (dismiss)="unpop($event)" />
      </bs-card>
    </ng-template>
  `,
  styles: [`
    :host { display: inline-block; }
    .spark-notification-count { font-size: .65rem; }
    .spark-notification-menu-list { width: 22rem; max-width: calc(100vw - 2rem); max-height: 60vh; overflow-y: auto; }
    .spark-notification-popout { width: 22rem; max-width: calc(100vw - 2rem); overflow: hidden; margin-top: .25rem; }
    .spark-notification-menu-list, .spark-notification-popout { --bs-list-group-bg: transparent; }
  `],
})
export class SparkNotificationBellComponent {
  protected readonly notifications = inject(SparkNotificationsService);
  private readonly language = inject(SparkLanguageService);
  private readonly options = inject(SPARK_NOTIFICATIONS_OPTIONS);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  /** Where "All notifications" goes. Default: `withNotifications({ listLink })`, else the `notifications` query page. */
  readonly allLink = input<string | readonly unknown[] | null>(null);

  readonly open = signal(false);
  /** What the pop-out shows, newest first. */
  readonly popped = signal<SparkBellEntry[]>([]);

  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();
  private paused = false;

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

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => this.clearTimers());
    if (!this.browser) return;

    // A rendered bell is where `notify` lands: SparkSessionNotifications warns while none is.
    const release = this.notifications.session.attachHost();
    destroyRef.onDestroy(release);

    this.notifications.arrived.pipe(takeUntilDestroyed()).subscribe(entry => this.pop(entry));

    // The menu shows what the pop-out would: opening it ends the pop-out and reads this session's entries.
    effect(() => {
      if (!this.open()) return;
      untracked(() => {
        this.clearTimers();
        this.popped.set([]);
        this.notifications.session.markAllRead();
      });
    });
  }

  protected t(key: string): string {
    return this.language.t('notifications.spa.' + key, SPARK_NOTIFICATION_TEXTS[key]);
  }

  setOpen(open: boolean): void {
    this.open.set(open);
  }

  close(): void {
    this.open.set(false);
  }

  /** A click outside closes the menu, except on the bell itself, whose own handler toggles it. */
  onOutsideClick(event: MouseEvent, toggle: HTMLElement): void {
    if (event.target instanceof Node && toggle.contains(event.target)) return;
    this.close();
  }

  async choose(entry: SparkBellEntry): Promise<void> {
    if (entry.source === 'server') this.close();
    await this.notifications.choose(entry);
  }

  async markAllRead(): Promise<void> {
    await this.notifications.markAllRead();
  }

  // --- the pop-out -------------------------------------------------------------------------------

  async chooseFromPopout(entry: SparkBellEntry): Promise<void> {
    this.unpop(entry);
    await this.notifications.choose(entry);
  }

  /** Closes an entry in the pop-out; it stays in the bell. */
  unpop(entry: SparkBellEntry): void {
    this.stopTimer(entry.key);
    this.popped.update(list => list.filter(e => e.key !== entry.key));
  }

  pause(): void {
    this.paused = true;
    this.clearTimers();
  }

  /** Every entry gets its full time again after a hover: it was being read. */
  resume(): void {
    this.paused = false;
    for (const entry of this.popped()) this.startTimer(entry);
  }

  private pop(entry: SparkBellEntry): void {
    if (this.open()) return;
    if (entry.source === 'server' && !this.firstShowing(entry.id)) return;
    const list = [entry, ...this.popped().filter(e => e.key !== entry.key)];
    for (const dropped of list.slice(SPARK_POPOUT_MAX)) this.stopTimer(dropped.key);
    this.popped.set(list.slice(0, SPARK_POPOUT_MAX));
    if (!this.paused) this.startTimer(entry);
  }

  private startTimer(entry: SparkBellEntry): void {
    this.stopTimer(entry.key);
    if (entry.kind === NotificationKind.Error || entry.durationMs === 0) return;
    const duration = entry.durationMs ?? SPARK_POPOUT_DEFAULT_MS;
    this.timers.set(entry.key, setTimeout(() => this.unpop(entry), duration));
  }

  private stopTimer(key: string): void {
    const timer = this.timers.get(key);
    if (timer === undefined) return;
    clearTimeout(timer);
    this.timers.delete(key);
  }

  private clearTimers(): void {
    for (const timer of this.timers.values()) clearTimeout(timer);
    this.timers.clear();
  }

  /**
   * Whether this browser has not shown the stored notification yet, remembering it if so: another tab, or the next
   * page, pops it out once only. Without storage (private mode, blocked site data) it pops out per page instead.
   */
  private firstShowing(id: string): boolean {
    try {
      const shown: string[] = JSON.parse(localStorage.getItem(SHOWN_KEY) ?? '[]');
      if (Array.isArray(shown) && shown.includes(id)) return false;
      const next = [...(Array.isArray(shown) ? shown : []), id].slice(-SHOWN_MAX);
      localStorage.setItem(SHOWN_KEY, JSON.stringify(next));
    } catch {
      // No storage: nothing to dedupe against.
    }
    return true;
  }
}
