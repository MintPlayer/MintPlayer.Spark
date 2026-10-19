import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import { EntityAttributeDefinition, asPackagingLevels, asQuantity } from '@mintplayer/ng-spark/models';
import { SparkAttributeEditRenderer } from './spark-attribute-renderer';

/**
 * The core `quantity` renderer's edit half (warehouse WX8): an amount input beside a unit picker.
 *
 * `rendererOptions`:
 * - `units`: the unit codes to offer, e.g. `["EA", "CS"]`. One code shows it as a fixed label.
 * - `baseUnit` + `levels` (`[{ "unit": "CS", "baseQuantity": 12 }]`): offer the base unit and every
 *   packaging level when `units` is not given.
 * - `unit`: the unit a new value starts in (default: the first offered, else `EA`).
 *
 * The value is sent in the unit picked; converting to the base unit is the server's job, exactly and
 * through the item's packaging levels.
 */
@Component({
  selector: 'spark-quantity-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quantity-editor.component.html',
  styleUrl: './quantity-editor.component.scss',
})
export class SparkQuantityEditorComponent implements SparkAttributeEditRenderer {
  readonly value = input<any>();
  readonly attribute = input<EntityAttributeDefinition | undefined>();
  readonly options = input<Record<string, any> | undefined>();
  readonly valueChange = input<(value: any) => void>(() => {});

  private readonly configured = computed<string[]>(() => {
    const options = this.options();
    const list = options?.['units'];
    if (Array.isArray(list)) return list.filter((u): u is string => typeof u === 'string' && u !== '').map(u => u.toUpperCase());
    const baseUnit = options?.['baseUnit'];
    if (typeof baseUnit !== 'string' || baseUnit === '') return [];
    return [baseUnit.toUpperCase(), ...asPackagingLevels(options?.['levels']).map(level => level.unit.toUpperCase())];
  });

  private readonly defaultUnit = computed(() => {
    const option = this.options()?.['unit'];
    if (typeof option === 'string' && option !== '') return option.toUpperCase();
    return this.configured()[0] ?? 'EA';
  });

  /** The amount as typed; it does not echo `12` back over `12.0`, so trailing zeros can be typed. */
  protected readonly amountText = linkedSignal<number | null, string>({
    source: () => asQuantity(this.value())?.Amount ?? null,
    computation: (amount, previous) => {
      if (amount === null) return '';
      return previous && previous.value.trim() !== '' && Number(previous.value) === amount ? previous.value : String(amount);
    },
  });

  /** The unit picked; kept while there is no value yet, so a unit chosen first does not snap back. */
  protected readonly unit = linkedSignal<string | null, string>({
    source: () => asQuantity(this.value())?.Unit ?? null,
    computation: (code, previous) => code ?? previous?.value ?? this.defaultUnit(),
  });

  protected readonly choices = computed(() => {
    const codes = new Set(this.configured());
    const current = asQuantity(this.value())?.Unit;
    if (current) codes.add(current);
    codes.add(this.defaultUnit());
    return [...codes];
  });

  protected readonly fixedUnit = computed(() => this.choices().length === 1);

  protected onAmountInput(event: Event): void {
    this.amountText.set((event.target as HTMLInputElement).value);
    this.emit();
  }

  protected onUnitChange(event: Event): void {
    this.unit.set((event.target as HTMLSelectElement).value);
    if (this.amountText().trim() !== '') this.emit();
  }

  private emit(): void {
    const text = this.amountText().trim();
    const amount = Number(text);
    if (text === '') this.valueChange()(null);
    else if (Number.isFinite(amount)) this.valueChange()({ Amount: amount, Unit: this.unit() });
  }
}
