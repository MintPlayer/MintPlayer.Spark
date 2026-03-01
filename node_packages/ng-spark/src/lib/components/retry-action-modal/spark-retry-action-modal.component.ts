import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { RetryActionService } from '../../services/retry-action.service';

@Component({
  selector: 'spark-retry-action-modal',
  imports: [CommonModule, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsButtonTypeDirective],
  template: `
    <bs-modal [isOpen]="isOpen()" (isOpenChange)="!$event && onOption('Cancel')">
      <div *bsModal>
        <div bsModalHeader>
          <h5 class="modal-title">{{ retryActionService.payload()?.title }}</h5>
        </div>
        @if (retryActionService.payload()?.message; as message) {
          <div bsModalBody>
            <p>{{ message }}</p>
          </div>
        }
        <div bsModalFooter>
          @for (option of retryActionService.payload()?.options; track option) {
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
export class SparkRetryActionModalComponent {
  protected readonly retryActionService = inject(RetryActionService);

  colors = Color;
  isOpen = computed(() => this.retryActionService.payload() !== null);

  onOption(option: string): void {
    const payload = this.retryActionService.payload();
    if (!payload) return;
    this.retryActionService.respond({
      step: payload.step,
      option,
      persistentObject: payload.persistentObject
    });
  }
}
