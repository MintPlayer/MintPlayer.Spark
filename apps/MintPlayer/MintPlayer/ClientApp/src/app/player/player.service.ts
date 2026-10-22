import { computed, Injectable, signal } from '@angular/core';

/** One queued medium: what plays, and the song (or artist) it came from. */
export interface QueueItem {
  url: string;
  /** The owner's breadcrumb, e.g. "Artist - Song". */
  title: string;
  /** The medium's type, e.g. "Official Audio". */
  subtitle?: string;
  ownerId?: string;
}

/**
 * The play queue behind the floating player (G7). As on the legacy site, adding to an idle queue starts playing
 * the new item; adding while something plays only queues it.
 */
@Injectable({ providedIn: 'root' })
export class PlayerService {
  readonly queue = signal<QueueItem[]>([]);
  readonly index = signal(-1);
  readonly current = computed(() => this.queue()[this.index()] ?? null);
  readonly hasPrevious = computed(() => this.index() > 0);
  readonly hasNext = computed(() => this.index() < this.queue().length - 1);
  readonly queueOpen = signal(false);

  add(item: QueueItem) {
    this.queue.update(queue => [...queue, item]);
    if (!this.current()) this.index.set(this.queue().length - 1);
  }

  playAt(index: number) {
    if (index >= 0 && index < this.queue().length) this.index.set(index);
  }

  previous() {
    if (this.hasPrevious()) this.index.update(i => i - 1);
  }

  /** Also what the end of a medium does; the last one simply stops. */
  next() {
    if (this.hasNext()) this.index.update(i => i + 1);
  }

  remove(index: number) {
    const current = this.index();
    this.queue.update(queue => queue.filter((_, i) => i !== index));
    // Removing the playing item moves on to the one that took its place (or the new last one).
    if (index < current || (index === current && current >= this.queue().length)) this.index.set(current - 1);
  }

  clear() {
    this.queue.set([]);
    this.index.set(-1);
  }
}
