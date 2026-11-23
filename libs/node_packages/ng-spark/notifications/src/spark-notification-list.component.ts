import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { BsCloseComponent } from '@mintplayer/ng-bootstrap/close';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { NotificationKind } from '@mintplayer/ng-spark/client-operations';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { sparkRelativeTime } from './relative-time';
import { SparkBellEntry } from './spark-notification';
import { SPARK_NOTIFICATION_TEXTS } from './spark-push-toggle.component';

const KIND_ICONS: Record<NotificationKind, string> = {
  [NotificationKind.Info]: 'info-circle',
  [NotificationKind.Success]: 'check-circle',
  [NotificationKind.Warning]: 'exclamation-triangle',
  [NotificationKind.Error]: 'x-circle',
};

const KIND_COLORS: Record<NotificationKind, string> = {
  [NotificationKind.Info]: 'text-info',
  [NotificationKind.Success]: 'text-success',
  [NotificationKind.Warning]: 'text-warning',
  [NotificationKind.Error]: 'text-danger',
};

/**
 * The rows of the bell (#710): one list for stored and session notifications, shown in the bell's menu and, for what
 * just arrived, in its pop-out. Each row has the kind's icon (the bell for a stored notification), the title, the
 * message and the relative time; choosing it emits {@link choose}. Session rows, and every row of the pop-out
 * (`dismissible`), can be closed.
 */
@Component({
  selector: 'spark-notification-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsListGroupComponent, BsListGroupItemComponent, BsCloseComponent, SparkIconComponent],
  template: `
    <bs-list-group [flush]="true" class="spark-notification-list">
      @for (entry of entries(); track entry.key) {
        <bs-list-group-item class="spark-notification" [class.spark-notification-unread]="!entry.isRead"
                            [attr.data-id]="entry.id" [attr.data-source]="entry.source">
          <div class="d-flex align-items-start gap-2">
            <spark-icon class="spark-notification-icon flex-shrink-0" [class]="iconColor(entry)" [name]="icon(entry)" />
            <button type="button" class="spark-notification-choose flex-grow-1 text-start" (click)="choose.emit(entry)">
              <span class="spark-notification-title d-block text-wrap" [class.fw-semibold]="!entry.isRead">{{ entry.title }}</span>
              @if (entry.body) {
                <span class="spark-notification-body d-block small text-body-secondary text-wrap">{{ entry.body }}</span>
              }
              <span class="spark-notification-time d-block small text-body-secondary">{{ when(entry) }}</span>
            </button>
            @if (dismissible() || entry.source === 'session') {
              <bs-close class="spark-notification-dismiss flex-shrink-0" [label]="t('dismiss')" (click)="dismiss.emit(entry)" />
            }
          </div>
        </bs-list-group-item>
      } @empty {
        <bs-list-group-item class="text-body-secondary spark-notification-empty">{{ t('empty') }}</bs-list-group-item>
      }
    </bs-list-group>
  `,
  styles: [`
    :host { display: block; }
    .spark-notification-choose { background: none; border: 0; padding: 0; color: inherit; font: inherit; min-width: 0; }
    .spark-notification-choose:focus-visible { outline: 2px solid var(--bs-focus-ring-color, currentColor); outline-offset: 2px; }
    .spark-notification-icon { margin-top: .15rem; }
  `],
})
export class SparkNotificationListComponent {
  private readonly language = inject(SparkLanguageService);

  readonly entries = input.required<readonly SparkBellEntry[]>();
  /** Whether every row can be closed, not only session rows (the pop-out). */
  readonly dismissible = input(false);

  readonly choose = output<SparkBellEntry>();
  readonly dismiss = output<SparkBellEntry>();

  protected icon(entry: SparkBellEntry): string {
    return entry.kind === null ? 'bell' : KIND_ICONS[entry.kind] ?? 'info-circle';
  }

  protected iconColor(entry: SparkBellEntry): string {
    return entry.kind === null ? 'text-body-secondary' : KIND_COLORS[entry.kind] ?? 'text-info';
  }

  protected when(entry: SparkBellEntry): string {
    return sparkRelativeTime(entry.createdAt, Date.now(), this.language.language());
  }

  protected t(key: string): string {
    return this.language.t('notifications.spa.' + key, SPARK_NOTIFICATION_TEXTS[key]);
  }
}
