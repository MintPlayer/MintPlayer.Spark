import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject, PersistentObjectAttribute } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDiffLine, diffHasChanges, lineDiff } from './line-diff';

/**
 * Where the `lineDiff` renderer finds the text to compare with — its `rendererOptions`:
 *
 * - `compareType`: the model name (or alias, or id) of the object to load;
 * - its id: `compareIdAttribute` (an attribute of this object holding it), or
 *   `compareIdPattern` + `compareIdReplacement` (a regular expression and replacement applied to this
 *   object's id);
 * - `compareAttribute`: the attribute of that object holding the text — or, for an AsDetail list, the
 *   list, with `compareRowKeyPattern` + `compareRowKeyReplacement` (deriving the row key from this
 *   object's id) and `compareRowAttribute` (the text on the row); for a single AsDetail object,
 *   `compareRowAttribute` alone.
 *
 * Contributions seeds these on a generated contribution type's text values, comparing against the
 * target's row of the same slot (the current version).
 */
export interface SparkLineDiffSource {
  type: string;
  id: string;
  attribute: string;
  rowKey?: string;
  rowAttribute?: string;
}

/** The comparison source for `item`, or null when the options do not describe one. */
export function lineDiffSourceOf(options: Record<string, any> | null | undefined, item: PersistentObject | null | undefined): SparkLineDiffSource | null {
  if (!options || !item) return null;
  const type = asText(options['compareType']);
  const attribute = asText(options['compareAttribute']);
  if (!type || !attribute) return null;

  const idAttribute = asText(options['compareIdAttribute']);
  const id = idAttribute
    ? asText(item.attributes?.find(a => a.name === idAttribute)?.value)
    : derive(item.id, options['compareIdPattern'], options['compareIdReplacement']);
  if (!id) return null;

  const rowKeyPattern = options['compareRowKeyPattern'];
  const rowKey = rowKeyPattern !== undefined ? derive(item.id, rowKeyPattern, options['compareRowKeyReplacement']) : undefined;
  if (rowKeyPattern !== undefined && rowKey === null) return null;
  const rowAttribute = asText(options['compareRowAttribute']) ?? undefined;
  return { type, id, attribute, ...(rowKey !== undefined && rowKey !== null ? { rowKey } : {}), ...(rowAttribute ? { rowAttribute } : {}) };
}

/** The text at `source` in the loaded object, or null when it is not there (or not readable). */
export function lineDiffTextOf(po: PersistentObject | null | undefined, source: SparkLineDiffSource): string | null {
  const attribute = po?.attributes?.find(a => a.name === source.attribute);
  if (!attribute) return null;
  if (source.rowKey !== undefined) {
    const row = attribute.objects?.find(o => o.id === source.rowKey);
    // No row for the slot: there is no current version (all hidden) — compare with nothing.
    if (!row) return '';
    return valueText(row.attributes?.find(a => a.name === source.rowAttribute));
  }
  if (source.rowAttribute && attribute.object !== undefined) {
    return attribute.object ? valueText(attribute.object.attributes?.find(a => a.name === source.rowAttribute)) : '';
  }
  return valueText(attribute);
}

function valueText(attribute: PersistentObjectAttribute | undefined): string | null {
  if (!attribute) return null;
  const value = attribute.value;
  return value === null || value === undefined ? '' : String(value);
}

function asText(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

function derive(id: string | undefined, pattern: unknown, replacement: unknown): string | null {
  if (!id || typeof pattern !== 'string') return null;
  let regex: RegExp;
  try { regex = new RegExp(pattern); } catch { return null; }
  if (!regex.test(id)) return null;
  return id.replace(regex, typeof replacement === 'string' ? replacement : '');
}

/**
 * The generic `lineDiff` detail renderer: this attribute's text, diffed line by line against a text
 * elsewhere ({@link SparkLineDiffSource}) — added lines marked `+`, removed ones `−`. When the other
 * object cannot be read (no right, gone), the text is shown as it is, with a note.
 *
 * Register through `sparkContributionRenderers` (or on its own: `{ name: 'lineDiff', detailComponent:
 * SparkLineDiffComponent }`).
 */
@Component({
  selector: 'spark-line-diff',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (state()) {
      @case ('loading') {
        <pre class="spark-line-diff-plain mb-0">{{ text() }}</pre>
      }
      @case ('diff') {
        @if (changed()) {
          <div class="spark-line-diff" role="table" [attr.aria-label]="attribute()?.name">
            @for (line of lines(); track $index) {
              <div role="row" [class]="'spark-line-diff-line spark-line-diff-' + line.kind">
                <span class="spark-line-diff-marker" role="cell"
                      [attr.aria-label]="line.kind === 'added' ? ('common.diffAdded' | t) : line.kind === 'removed' ? ('common.diffRemoved' | t) : null">{{ marker(line) }}</span>
                <span class="spark-line-diff-text" role="cell">{{ line.text }}</span>
              </div>
            }
          </div>
        } @else {
          <pre class="spark-line-diff-plain mb-1">{{ text() }}</pre>
          <div class="small text-muted spark-line-diff-identical">{{ 'common.diffIdentical' | t }}</div>
        }
      }
      @default {
        <pre class="spark-line-diff-plain mb-1">{{ text() }}</pre>
        <div class="small text-muted spark-line-diff-unavailable">{{ 'common.diffUnavailable' | t }}</div>
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .spark-line-diff-plain { white-space: pre-wrap; font-family: inherit; }
    .spark-line-diff { font-family: var(--bs-font-monospace, monospace); font-size: .875em; border: 1px solid var(--bs-border-color, #dee2e6); border-radius: var(--bs-border-radius, .375rem); overflow-x: auto; }
    .spark-line-diff-line { display: flex; white-space: pre-wrap; }
    .spark-line-diff-marker { flex: 0 0 1.5rem; text-align: center; user-select: none; color: var(--bs-secondary-color, #6c757d); }
    .spark-line-diff-text { flex: 1 1 auto; min-width: 0; padding-right: .5rem; }
    .spark-line-diff-added { background: var(--bs-success-bg-subtle, #d1e7dd); color: var(--bs-success-text-emphasis, #0a3622); }
    .spark-line-diff-removed { background: var(--bs-danger-bg-subtle, #f8d7da); color: var(--bs-danger-text-emphasis, #58151c); text-decoration: line-through; }
  `],
})
export class SparkLineDiffComponent {
  private readonly spark = inject(SparkService);

  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  item = input<PersistentObject | undefined>();

  protected readonly text = computed(() => {
    const value = this.value();
    return value === null || value === undefined ? '' : String(value);
  });

  /** The compared text: undefined while loading, null when it could not be read. */
  private readonly compared = signal<string | null | undefined>(undefined);

  protected readonly state = computed<'loading' | 'diff' | 'unavailable'>(() => {
    const compared = this.compared();
    return compared === undefined ? 'loading' : compared === null ? 'unavailable' : 'diff';
  });

  protected readonly lines = computed<SparkDiffLine[]>(() => lineDiff(this.compared() ?? '', this.text()));
  protected readonly changed = computed(() => diffHasChanges(this.lines()));

  private generation = 0;

  constructor() {
    effect(() => {
      const source = lineDiffSourceOf(this.options(), this.item());
      untracked(() => void this.load(source));
    });
  }

  protected marker(line: SparkDiffLine): string {
    return line.kind === 'added' ? '+' : line.kind === 'removed' ? '−' : ' ';
  }

  private async load(source: SparkLineDiffSource | null): Promise<void> {
    const generation = ++this.generation;
    if (!source) { this.compared.set(null); return; }
    this.compared.set(undefined);
    try {
      const type = await this.typeIdOf(source.type);
      const po = await this.spark.get(type, source.id);
      if (generation === this.generation) this.compared.set(lineDiffTextOf(po, source));
    } catch {
      if (generation === this.generation) this.compared.set(null);
    }
  }

  /** The entity type id for a model name or alias (the load endpoint is keyed by the type's id). */
  private async typeIdOf(type: string): Promise<string> {
    const types = await this.spark.getEntityTypes();
    return types.find(t => t.name === type || t.alias === type || t.id === type)?.id ?? type;
  }
}
