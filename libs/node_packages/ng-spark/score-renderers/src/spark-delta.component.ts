import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { toSparkScoreNumber } from './score-bands';
import { numberOption, stringOption } from './option-readers';

/** `rendererOptions` of the `spark-delta` renderer. Every member is optional. */
export interface SparkDeltaOptions {
  /** Decimals of the shown change. Default 1. */
  decimals?: number;
  /** A rise is good (green) and a drop bad (red). Set false for a cost, an error count, a load time. Default true. */
  higherIsBetter?: boolean;
  /** Text after the number, e.g. `' pt'` or `'%'`. Default none. */
  suffix?: string;
  /** Text for "no comparison". Default `'—'`. */
  empty?: string;
}

/** How a delta reads: the text and its tone. */
export interface SparkDeltaView {
  /** The raw change is positive. Rounding does not change it: +0.04 at one decimal is `+0.0`, still up. */
  up: boolean;
  /** The raw change is negative. */
  down: boolean;
  /** `+1.3`, `-0.3`, `0.0`, or the empty text when there is nothing to compare. */
  text: string;
  /** `text-success`, `text-danger` or `text-muted`, honouring `higherIsBetter`. */
  tone: 'text-success' | 'text-danger' | 'text-muted';
}

/**
 * Formats a signed change. No value (null, undefined, empty, unparseable) is the empty text and
 * never `0.0`, which would claim a comparison that did not happen; an exact zero is a real, neutral
 * comparison.
 */
export function formatSparkDelta(raw: unknown, options?: SparkDeltaOptions | Record<string, any> | null): SparkDeltaView {
  const o = (options ?? {}) as SparkDeltaOptions;
  const value = toSparkScoreNumber(raw);
  if (value === null) return { up: false, down: false, text: stringOption(o.empty, '—'), tone: 'text-muted' };

  const decimals = Math.min(100, Math.max(0, Math.trunc(numberOption(o.decimals, 1))));
  const up = value > 0;
  const down = value < 0;
  const good = o.higherIsBetter === false ? down : up;
  const bad = o.higherIsBetter === false ? up : down;
  return {
    up,
    down,
    text: `${up ? '+' : ''}${value.toFixed(decimals)}${stringOption(o.suffix, '')}`,
    tone: good ? 'text-success' : bad ? 'text-danger' : 'text-muted',
  };
}

/**
 * The `spark-delta` renderer: a signed change, green when it improves and red when it worsens
 * (`higherIsBetter`), muted for no change, and a muted dash when there is nothing to compare.
 *
 * Generalised from CodeCoverage's coverage delta, which is this renderer with its defaults.
 */
@Component({
  selector: 'spark-delta',
  template: `<span class="small" [class]="view().tone">{{ view().text }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkDeltaComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly view = computed(() => formatSparkDelta(this.value(), this.options()));
}
