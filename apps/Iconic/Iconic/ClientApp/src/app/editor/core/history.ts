/**
 * Undo and redo. Scene edits record the whole scene before the change: scenes are small JSON (a big one is a few
 * hundred KB), so snapshots are simplest. Paint strokes record the pixels of the one bitmap they changed.
 */
export class History<T> {
  private past: T[] = [];
  private future: T[] = [];

  constructor(private readonly limit = 200) {}

  /** Records an undoable step. */
  push(entry: T): void {
    this.past.push(entry);
    if (this.past.length > this.limit) this.past.shift();
    this.future = [];
  }

  /** The step to undo; `inverse` turns it into the step that redoes it. */
  undo(inverse: (entry: T) => T): T | null {
    const entry = this.past.pop();
    if (entry === undefined) return null;
    this.future.push(inverse(entry));
    return entry;
  }

  redo(inverse: (entry: T) => T): T | null {
    const entry = this.future.pop();
    if (entry === undefined) return null;
    this.past.push(inverse(entry));
    return entry;
  }

  get canUndo(): boolean { return this.past.length > 0; }
  get canRedo(): boolean { return this.future.length > 0; }

  clear(): void {
    this.past = [];
    this.future = [];
  }
}
