import { ChangeDetectionStrategy, ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsModalModule } from '@mintplayer/ng-bootstrap/modal';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { Subscription } from 'rxjs';
import { RetryActionService } from '../../core/services/retry-action.service';
import { RetryActionPayload } from '../../core/models/retry-action';

@Component({
  selector: 'app-retry-action-modal',
  imports: [CommonModule, BsModalModule, BsButtonTypeDirective],
  template: `
    <bs-modal [(isOpen)]="isOpen" (isOpenChange)="!$event && onOption('Cancel')">
      <div *bsModal>
        <div bsModalHeader>
          <h5 class="modal-title">{{ payload?.title }}</h5>
        </div>
        @if (payload?.message; as message) {
          <div bsModalBody>
            <p>{{ message }}</p>
          </div>
        }
        <div bsModalFooter>
          @for (option of payload?.options; track option) {
            <button
              type="button"
              [color]="option === 'Cancel' ? colors.secondary : colors.primary"
              (click)="onOption(option)">
              {{ option }}
            </button>
          }
        </div>
      </div>
    </bs-modal>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RetryActionModalComponent implements OnInit, OnDestroy {
  private readonly retryActionService = inject(RetryActionService);
  private readonly cdr = inject(ChangeDetectorRef);
  private subscription?: Subscription;

  colors = Color;
  isOpen = false;
  payload: RetryActionPayload | null = null;

  ngOnInit(): void {
    this.subscription = this.retryActionService.payload$.subscribe(payload => {
      this.payload = payload;
      this.isOpen = true;
      this.cdr.markForCheck();
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  onOption(option: string): void {
    if (!this.payload) return;
    this.isOpen = false;
    this.retryActionService.respond({
      step: this.payload.step,
      option,
      persistentObject: this.payload.persistentObject
    });
    this.payload = null;
    this.cdr.markForCheck();
  }
}
