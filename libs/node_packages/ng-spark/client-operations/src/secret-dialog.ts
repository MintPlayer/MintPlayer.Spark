import {
    ApplicationRef,
    ChangeDetectionStrategy,
    Component,
    type ComponentRef,
    DestroyRef,
    EnvironmentInjector,
    Injectable,
    PLATFORM_ID,
    computed,
    createComponent,
    inject,
    signal,
} from '@angular/core';
import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import {
    BsModalBodyDirective,
    BsModalDirective,
    BsModalFooterDirective,
    BsModalHeaderDirective,
    BsModalHostComponent,
} from '@mintplayer/ng-bootstrap/modal';
import { resolveTranslation, type TranslatedString } from '@mintplayer/ng-spark/models';

/** A value shown once: the `showSecret` operation's payload, or a value a page received itself. */
export interface SparkSecret {
    title: string;
    message: string;
    value: string;
}

/**
 * Shows a value the server will never show again, in a modal with a copy button (`showSecret`,
 * `docs/identity_provider_platform_PRD.md` D5).
 *
 * The dialog mounts itself on `document.body` the first time it is needed, so an application does not
 * have to place a component in its root template for `IClientAccessor.ShowSecret` to work: a handler
 * whose dialog nobody placed would lose the value as surely as a missing handler.
 *
 * The value lives only in this service's signal while the dialog is open. Closing the dialog clears
 * it; it is never written to the URL, the router state or browser storage.
 */
@Injectable({ providedIn: 'root' })
export class SparkSecretDialogService {
    private readonly appRef = inject(ApplicationRef);
    private readonly injector = inject(EnvironmentInjector);
    private readonly document = inject(DOCUMENT);
    private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
    private readonly current = signal<SparkSecret | null>(null);
    private dialog: ComponentRef<SparkShowSecretModalComponent> | null = null;

    /** The secret on display, or null when the dialog is closed. */
    readonly secret = this.current.asReadonly();

    constructor() {
        inject(DestroyRef).onDestroy(() => {
            this.dialog?.destroy();
            this.dialog = null;
        });
    }

    show(secret: SparkSecret): void {
        if (!this.browser) return;
        this.mount();
        this.current.set({ ...secret });
    }

    /** Closes the dialog and discards the value. */
    close(): void {
        this.current.set(null);
    }

    private mount(): void {
        if (this.dialog) return;
        const ref = createComponent(SparkShowSecretModalComponent, { environmentInjector: this.injector });
        this.appRef.attachView(ref.hostView);
        this.document.body.appendChild(ref.location.nativeElement);
        this.dialog = ref;
    }
}

const TEXT: Record<'copy' | 'copied' | 'copyFailed' | 'close' | 'value', TranslatedString> = {
    copy: { en: 'Copy', fr: 'Copier', nl: 'Kopiëren' },
    copied: { en: 'Copied', fr: 'Copié', nl: 'Gekopieerd' },
    copyFailed: {
        en: 'Copying failed. Select the value and copy it yourself.',
        fr: 'La copie a échoué. Sélectionnez la valeur et copiez-la vous-même.',
        nl: 'Kopiëren mislukt. Selecteer de waarde en kopieer ze zelf.',
    },
    close: { en: 'Close', fr: 'Fermer', nl: 'Sluiten' },
    value: { en: 'Value', fr: 'Valeur', nl: 'Waarde' },
};

/** The dialog behind {@link SparkSecretDialogService}; mounted by the service, not by applications. */
@Component({
    selector: 'spark-show-secret-modal',
    imports: [
        BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective,
        BsButtonTypeDirective, BsFormComponent, BsFormControlDirective,
    ],
    template: `
        <bs-modal [isOpen]="isOpen()" (isOpenChange)="!$event && close()">
            <div *bsModal>
                <div bsModalHeader>
                    <h5 class="modal-title">{{ secret()?.title }}</h5>
                </div>
                <div bsModalBody>
                    @if (secret(); as s) {
                        <p class="spark-secret-message">{{ s.message }}</p>
                        <bs-form>
                            <input type="text" readonly class="font-monospace spark-secret-value" spellcheck="false"
                                   autocomplete="off" [attr.aria-label]="text().value" [value]="s.value"
                                   (focus)="selectAll($event)" />
                        </bs-form>
                        @if (copyState() === 'copied') {
                            <small class="text-success d-block mt-2 spark-secret-copied" role="status">{{ text().copied }}</small>
                        } @else if (copyState() === 'failed') {
                            <small class="text-danger d-block mt-2 spark-secret-copy-failed" role="alert">{{ text().copyFailed }}</small>
                        }
                    }
                </div>
                <div bsModalFooter>
                    <button type="button" class="spark-secret-copy" [color]="colors.primary" (click)="copy()">{{ text().copy }}</button>
                    <button type="button" class="spark-secret-close" [color]="colors.secondary" (click)="close()">{{ text().close }}</button>
                </div>
            </div>
        </bs-modal>
    `,
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShowSecretModalComponent {
    private readonly dialog = inject(SparkSecretDialogService);
    private readonly document = inject(DOCUMENT);
    private copiedTimer: ReturnType<typeof setTimeout> | undefined;

    protected readonly colors = Color;
    protected readonly secret = this.dialog.secret;
    protected readonly isOpen = computed(() => this.secret() !== null);
    protected readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
    protected readonly text = computed(() => ({
        copy: resolveTranslation(TEXT.copy),
        copied: resolveTranslation(TEXT.copied),
        copyFailed: resolveTranslation(TEXT.copyFailed),
        close: resolveTranslation(TEXT.close),
        value: resolveTranslation(TEXT.value),
    }));

    protected selectAll(event: Event): void {
        (event.target as HTMLInputElement | null)?.select();
    }

    protected async copy(): Promise<void> {
        const value = this.secret()?.value;
        if (!value) return;
        try {
            const clipboard = this.document.defaultView?.navigator.clipboard;
            if (!clipboard) throw new Error('Clipboard API unavailable');
            await clipboard.writeText(value);
            this.copyState.set('copied');
            clearTimeout(this.copiedTimer);
            this.copiedTimer = setTimeout(() => this.copyState.set('idle'), 2500);
        } catch {
            this.copyState.set('failed');
        }
    }

    protected close(): void {
        clearTimeout(this.copiedTimer);
        this.copyState.set('idle');
        this.dialog.close();
    }
}
