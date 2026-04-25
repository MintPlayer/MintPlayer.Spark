import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SparkNotificationService } from './notification.service';
import { NotificationKind } from './instructions';

@Component({
    selector: 'spark-toast-container',
    standalone: true,
    template: `
        <div class="spark-toast-container">
            @for (toast of notifications.toasts(); track toast.id) {
                <div
                    class="spark-toast"
                    [class.spark-toast--info]="toast.kind === Kind.Info"
                    [class.spark-toast--success]="toast.kind === Kind.Success"
                    [class.spark-toast--warning]="toast.kind === Kind.Warning"
                    [class.spark-toast--error]="toast.kind === Kind.Error"
                    (click)="notifications.dismiss(toast.id)"
                >
                    {{ toast.message }}
                </div>
            }
        </div>
    `,
    styles: [`
        .spark-toast-container {
            position: fixed;
            top: 1rem;
            right: 1rem;
            z-index: 9999;
            display: flex;
            flex-direction: column;
            gap: 0.5rem;
            pointer-events: none;
        }
        .spark-toast {
            padding: 0.75rem 1rem;
            border-radius: 4px;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.15);
            cursor: pointer;
            min-width: 220px;
            max-width: 400px;
            color: white;
            font-size: 0.95rem;
            pointer-events: auto;
            animation: spark-toast-in 0.18s ease-out;
        }
        .spark-toast--info { background: #0d6efd; }
        .spark-toast--success { background: #198754; }
        .spark-toast--warning { background: #ffc107; color: #000; }
        .spark-toast--error { background: #dc3545; }
        @keyframes spark-toast-in {
            from { opacity: 0; transform: translateX(8px); }
            to { opacity: 1; transform: translateX(0); }
        }
    `],
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkToastContainerComponent {
    protected readonly notifications = inject(SparkNotificationService);
    protected readonly Kind = NotificationKind;
}
