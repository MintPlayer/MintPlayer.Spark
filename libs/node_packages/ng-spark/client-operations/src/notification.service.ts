import { Injectable, signal } from '@angular/core';
import { NotificationKind } from './operations';

export interface SparkToast {
    id: string;
    message: string;
    kind: NotificationKind;
    durationMs: number;
}

const DEFAULT_DURATION_MS = 4000;

/**
 * Holds the active toasts as a signal. The `<spark-toast-container>` component
 * renders them; the built-in `notify` operation handler pushes new toasts here.
 *
 * Auto-dismissal: each toast schedules its own removal after `durationMs`. Pass
 * `0` to make a toast sticky (manual dismissal only).
 */
@Injectable({ providedIn: 'root' })
export class SparkNotificationService {
    private readonly _toasts = signal<readonly SparkToast[]>([]);
    readonly toasts = this._toasts.asReadonly();

    show(message: string, kind: NotificationKind = NotificationKind.Info, durationMs?: number): void {
        const id = typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
        const effectiveDuration = durationMs ?? DEFAULT_DURATION_MS;
        const toast: SparkToast = { id, message, kind, durationMs: effectiveDuration };
        this._toasts.update(toasts => [...toasts, toast]);

        if (effectiveDuration > 0) {
            setTimeout(() => this.dismiss(id), effectiveDuration);
        }
    }

    dismiss(id: string): void {
        this._toasts.update(toasts => toasts.filter(t => t.id !== id));
    }

    clear(): void {
        this._toasts.set([]);
    }
}
