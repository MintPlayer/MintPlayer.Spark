import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, input, output, viewChildren } from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { InputTypePipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/**
 * Editor for a list of plain values — an attribute with `isArray` and a primitive `dataType`, such as
 * a C# `List<string>` (see `isPrimitiveArrayAttribute`). One input per item, each the same control
 * the form draws for a single value of that data type, with a remove button per item and an add
 * button under the list.
 *
 * Who may change the list is decided by the attribute rights, as for any other attribute: an
 * attribute the caller may not `Edit` (or `New`, on a create) arrives read-only, and the server keeps
 * the stored list whatever is posted. A list has no row type, so the row-type `New`/`Edit`/`Delete`
 * rights an AsDetail grid asks for do not exist here: adding, removing and editing an item are all
 * edits of the one attribute. `readonly` hides the add/remove buttons and shows the items as text.
 *
 * Every change emits a new array (never a mutation of the one passed in), so the host's change
 * detection and its "did this attribute change" comparison both see it.
 */
@Component({
  selector: 'spark-primitive-list-editor',
  imports: [CdkDropList, CdkDrag, CdkDragHandle, BsFormControlDirective, BsInputGroupComponent, BsButtonTypeDirective, SparkIconComponent, InputTypePipe, TranslateKeyPipe],
  templateUrl: './spark-primitive-list-editor.component.html',
  styleUrl: './spark-primitive-list-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPrimitiveListEditorComponent {
  /** The list as held by the form. Anything that is not an array (null, the form's '' default) is an empty list. */
  value = input<unknown>(null);
  /** The item data type, i.e. the attribute's `dataType`; picks each item's input type. */
  dataType = input<string>('string');
  /** Shows the items as text, without add/remove buttons. */
  readonly = input(false);
  /** Renders the inputs with the Bootstrap is-invalid state. */
  isInvalid = input(false);
  /** Forwarded to the first item's input, so the form's `<label for>` lands on it. */
  inputId = input<string | undefined>(undefined);
  /**
   * The attribute's `isSortable` (`[Sortable]` on the property): items get a drag handle and can be
   * reordered, as the rows of a sortable AsDetail grid can. Never while {@link readonly}.
   */
  sortable = input<boolean | null | undefined>(false);

  canReorder = computed(() => !!this.sortable() && !this.readonly());

  valueChange = output<unknown[]>();
  /** An item's input lost focus: the form's blur hook (rule display, `triggersRefresh` on blur). */
  itemBlur = output<void>();

  colors = Color;

  private readonly itemInputs = viewChildren<ElementRef<HTMLInputElement>>('itemInput');

  items = computed<unknown[]>(() => {
    const value = this.value();
    return Array.isArray(value) ? value : [];
  });

  isNumeric = computed(() => this.dataType() === 'number' || this.dataType() === 'decimal');

  /** The index {@link add} appended, focused as soon as its input exists. */
  private pendingFocus: number | null = null;

  constructor() {
    // Not afterNextRender (the new item exists only once the parent has written the emitted list back
    // into `value`) and not effect (it runs before the view refresh that creates the input). And the
    // focus itself waits a macrotask: the item's bs-input-group is inserted into the drop list after this
    // hook, and that insertion drops a focus given earlier (measured in the browser: focus() called,
    // then the insert, then activeElement still on the previous element).
    afterRenderEffect(() => {
      const inputs = this.itemInputs();
      if (this.pendingFocus === null || inputs.length <= this.pendingFocus) return;
      const element = inputs[this.pendingFocus].nativeElement;
      this.pendingFocus = null;
      setTimeout(() => element.focus());
    });
  }

  /** Appends an empty item and focuses its input once it renders. */
  add(): void {
    this.pendingFocus = this.items().length;
    this.valueChange.emit([...this.items(), this.isNumeric() ? null : '']);
  }

  remove(index: number): void {
    this.valueChange.emit(this.items().filter((_, i) => i !== index));
  }

  /** A drop of the drag handle: the same `moveItemInArray` the AsDetail grid's reorder uses, on a copy. */
  reorder(event: Pick<CdkDragDrop<unknown[]>, 'previousIndex' | 'currentIndex'>): void {
    if (!this.canReorder() || event.previousIndex === event.currentIndex) return;
    const next = [...this.items()];
    moveItemInArray(next, event.previousIndex, event.currentIndex);
    this.valueChange.emit(next);
  }

  update(index: number, raw: string): void {
    const next = [...this.items()];
    next[index] = this.parse(raw);
    this.valueChange.emit(next);
  }

  onInput(index: number, event: Event): void {
    const target = event.target as HTMLInputElement;
    // A number input mid-typing ("1.", "-") reports '' with `badInput` set. Emitting null for it
    // would re-bind `[value]` to '' and wipe what the user is typing; keep the last number instead.
    if (target.validity?.badInput) return;
    this.update(index, target.value);
  }

  /**
   * A number input hands back text; the server deserializes an `int[]` / `List<decimal>` from JSON
   * numbers and silently drops the whole list when an item is a string. A cleared number is null.
   */
  private parse(raw: string): unknown {
    if (!this.isNumeric()) return raw;
    if (raw.trim() === '') return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  /** Display text of an item in read-only mode. */
  display(item: unknown): string {
    return item === null || item === undefined ? '' : String(item);
  }
}
