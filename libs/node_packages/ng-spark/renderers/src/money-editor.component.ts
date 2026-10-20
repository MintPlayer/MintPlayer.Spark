import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import {
  EntityAttributeDefinition,
  asMoney,
  currencyChoices,
  currencyDisplayName,
  currencyFractionDigits,
} from '@mintplayer/ng-spark/models';
import { SparkAttributeEditRenderer } from './spark-attribute-renderer';

/**
 * The core `money` renderer's edit half (commerce X4): an amount input beside a currency picker.
 *
 * Standard platform only (K11): the amount is an `<input type="number">`, so the browser reads the
 * viewer's own decimal separator; the currencies come from `Intl.supportedValuesOf('currency')` and are
 * named with `Intl.DisplayNames`.
 *
 * `rendererOptions`:
 * - `currencies`: the codes to offer, e.g. `["EUR", "USD"]`. One code shows it as a fixed label instead of a picker.
 * - `currency`: the currency a new value starts in (default: the first offered, else `EUR`).
 *
 * The editor does not round. An amount with more decimals than the currency has is marked invalid here
 * and refused by the server (a 400 naming the attribute), because money is never rounded silently.
 */
@Component({
  selector: 'spark-money-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './money-editor.component.html',
  styleUrl: './money-editor.component.scss',
})
export class SparkMoneyEditorComponent implements SparkAttributeEditRenderer {
  readonly value = input<any>();
  readonly attribute = input<EntityAttributeDefinition | undefined>();
  readonly options = input<Record<string, any> | undefined>();
  readonly valueChange = input<(value: any) => void>(() => {});

  private readonly configured = computed<string[] | undefined>(() => {
    const list = this.options()?.['currencies'];
    return Array.isArray(list) ? list.filter((c): c is string => typeof c === 'string' && c !== '') : undefined;
  });

  private readonly defaultCurrency = computed(() => {
    const option = this.options()?.['currency'];
    if (typeof option === 'string' && option !== '') return option.toUpperCase();
    return this.configured()?.[0]?.toUpperCase() ?? 'EUR';
  });

  /**
   * The amount as typed. It follows the incoming value, except when that value is the one just typed:
   * echoing `12.5` back over `12.50` (or `12` over `12.0`) would make a trailing zero impossible to type.
   */
  protected readonly amountText = linkedSignal<number | null, string>({
    source: () => asMoney(this.value())?.Amount ?? null,
    computation: (amount, previous) => {
      if (amount === null) return '';
      return previous && previous.value.trim() !== '' && Number(previous.value) === amount ? previous.value : String(amount);
    },
  });

  /**
   * The currency picked. It follows the incoming value, and keeps the viewer's pick while there is no
   * value yet: a currency chosen before the amount is typed must not snap back to the default.
   */
  protected readonly currency = linkedSignal<string | null, string>({
    source: () => asMoney(this.value())?.Currency ?? null,
    computation: (code, previous) => code ?? previous?.value ?? this.defaultCurrency(),
  });

  protected readonly choices = computed(() =>
    currencyChoices(this.configured(), asMoney(this.value())?.Currency, this.defaultCurrency())
      .map(code => ({ code, label: `${code} · ${currencyDisplayName(code)}` })));

  protected readonly fixedCurrency = computed(() => this.choices().length === 1);

  protected readonly digits = computed(() => currencyFractionDigits(this.currency()));

  protected readonly step = computed(() => {
    const digits = this.digits();
    return digits === 0 ? '1' : `0.${'0'.repeat(digits - 1)}1`;
  });

  /** More decimals than the currency allows: the server will refuse it, so mark it before saving. */
  protected readonly tooPrecise = computed(() =>
    (this.amountText().split(/[.,]/)[1]?.length ?? 0) > this.digits());

  protected onAmountInput(event: Event): void {
    this.amountText.set((event.target as HTMLInputElement).value);
    this.emit();
  }

  protected onCurrencyChange(event: Event): void {
    this.currency.set((event.target as HTMLSelectElement).value);
    if (this.amountText().trim() !== '') this.emit();
  }

  private emit(): void {
    const text = this.amountText().trim();
    const amount = Number(text);
    // A cleared input clears the value; text the browser could not read as a number is not sent at all.
    if (text === '') this.valueChange()(null);
    else if (Number.isFinite(amount)) this.valueChange()({ Amount: amount, Currency: this.currency() });
  }
}
