import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import type { SparkWebhookOwner } from './models';
import { SparkAppsTextPipe } from './text';

/** The key a picked owner is stored under: kind and key, which together name it. */
export function ownerId(owner: { kind: string; key: string }): string {
  return `${owner.kind}|${owner.key}`;
}

/**
 * Picks whose webhooks, Apps or installations a page shows: the signed-in user or one of the organizations they
 * administer (`GET /spark/apps/webhooks/owners`). Hidden when there is only one.
 */
@Component({
  selector: 'spark-apps-owner-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BsSelectComponent, BsSelectOption, SparkAppsTextPipe],
  template: `
    @if (owners().length > 1) {
      <div class="mb-3 spark-apps-owner-picker">
        <bs-select [title]="'owner' | sparkApps" [attr.aria-label]="'owner' | sparkApps"
                   [ngModel]="selectedId()" (ngModelChange)="pick($event)">
          @for (owner of owners(); track id(owner)) {
            <option [ngValue]="id(owner)">{{ owner.displayName }}</option>
          }
        </bs-select>
      </div>
    }
  `,
})
export class SparkAppsOwnerPickerComponent {
  readonly owners = input.required<SparkWebhookOwner[]>();
  readonly selected = model<SparkWebhookOwner | null>(null);

  protected readonly id = ownerId;
  protected readonly selectedId = computed(() => {
    const selected = this.selected();
    return selected ? ownerId(selected) : null;
  });

  protected pick(id: string): void {
    this.selected.set(this.owners().find(owner => ownerId(owner) === id) ?? null);
  }
}
