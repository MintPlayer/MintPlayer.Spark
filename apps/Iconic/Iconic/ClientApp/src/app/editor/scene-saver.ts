import { signal } from '@angular/core';
import { apiError, type IconicApi, type ProjectKind, type Usage } from '../api/iconic-api';
import { diff } from './core/diff';
import { encodePng } from './core/formats/png';
import { toDataUrl } from './core/import';
import type { Id, Scene } from './core/model';
import { renderToImageData } from './core/render';
import { evaluate } from './core/evaluate';
import type { EditorStore } from './editor-store';

export type SaveState = 'idle' | 'dirty' | 'saving' | 'saved' | 'conflict' | 'quota' | 'error';

export const DRAFT_KEY = 'iconic.draft.v1';

export interface Draft {
  name: string;
  kind: ProjectKind;
  scene: Scene;
  savedAt: string;
}

export function readDraft(): Draft | null {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    return raw ? (JSON.parse(raw) as Draft) : null;
  } catch {
    return null;
  }
}

export function clearDraft(): void {
  try { localStorage.removeItem(DRAFT_KEY); } catch { /* storage blocked: nothing to clear */ }
}

/**
 * Saving (S5, D3, D21). An owned project saves a second after the last edit, as the patch from the last saved scene
 * (the whole scene when the patch would not be smaller). The server checks the version: a 409 means another tab
 * saved first, and saving stops until the user reloads. A draft lives in localStorage. Painted bitmaps upload as
 * attachments of their own; the thumbnail follows at most every 20 seconds.
 */
export class SceneSaver {
  readonly state = signal<SaveState>('idle');
  readonly message = signal<string | null>(null);
  readonly usage = signal<Usage | null>(null);

  private saved: Scene | null = null;
  private version = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private thumbnailTimer: ReturnType<typeof setTimeout> | null = null;
  private running: Promise<void> | null = null;
  private readonly dirtyPaint = new Set<string>();
  /**
   * Off until a drawing is opened, and while another one loads: the store's first (empty) scene must never be
   * written over the browser's draft.
   */
  private active = false;

  constructor(private readonly store: EditorStore, private readonly api: IconicApi) {}

  /** After opening an owned project: what the server has. */
  startOwned(version: number, scene: Scene, usage: Usage): void {
    this.active = true;
    this.saved = scene;
    this.version = version;
    this.usage.set(usage);
    this.state.set('saved');
    this.message.set(null);
  }

  /** Call after loading the draft into the store, so the load itself is not a change. */
  startDraft(): void {
    this.active = true;
    this.saved = null;
    this.state.set('idle');
    this.message.set(null);
  }

  stop(): void {
    this.active = false;
    if (this.timer) clearTimeout(this.timer);
    if (this.thumbnailTimer) clearTimeout(this.thumbnailTimer);
    this.timer = this.thumbnailTimer = null;
  }

  changed(scene: Scene): void {
    const mode = this.store.mode();
    if (!this.active || mode === 'shared') return;
    if (mode === 'owned' && (scene === this.saved || this.state() === 'conflict')) return;
    this.state.set('dirty');
    this.schedule(mode === 'draft' ? 400 : 1000);
  }

  paintChanged(layer: Id, width: number): void {
    if (!this.active || this.store.mode() === 'shared') return;
    this.dirtyPaint.add(`${layer}:${width}`);
    this.state.set('dirty');
    this.schedule(800);
  }

  /** Saves now (leaving the page, pressing Ctrl+S). */
  async flush(): Promise<void> {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    while (this.running) await this.running;
    this.running = this.run().finally(() => (this.running = null));
    await this.running;
  }

  private schedule(ms: number): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.timer = null; void this.flush(); }, ms);
  }

  private async run(): Promise<void> {
    if (!this.active) return;
    try {
      await this.savePaint();
      if (this.store.mode() === 'draft') this.saveDraft();
      else if (this.store.mode() === 'owned') await this.saveOwned();
    } catch (e) {
      const err = apiError(e);
      if (err.status === 409 && err.code === 'scene.version') {
        this.state.set('conflict');
        this.message.set('This drawing was saved from another tab or device. Reload to continue; edits since then are not saved.');
      } else if (err.status === 413 || err.code.startsWith('quota')) {
        this.state.set('quota');
        this.message.set(err.message);
      } else {
        this.state.set('error');
        this.message.set(err.message);
        // Offline or rate-limited: try again later.
        this.schedule(err.status === 0 || err.status === 429 ? 10_000 : 30_000);
      }
    }
  }

  private async saveOwned(): Promise<void> {
    const key = this.store.key();
    if (!key || !this.saved) return;
    const scene = this.store.scene();
    if (scene === this.saved) { this.state.set('saved'); return; }
    const ops = diff(this.saved, scene);
    this.state.set('saving');
    if (ops.length > 0) {
      const whole = JSON.stringify(scene).length;
      const result = JSON.stringify(ops).length < whole
        ? await this.api.patchScene(key, this.version, ops)
        : await this.api.saveScene(key, this.version, scene);
      this.version = result.version;
      this.usage.set(result.usage);
    }
    this.saved = scene;
    this.state.set(this.store.scene() === scene ? 'saved' : 'dirty');
    this.message.set(null);
    this.scheduleThumbnail();
  }

  private saveDraft(): void {
    const draft: Draft = { name: this.store.name(), kind: this.store.kind(), scene: this.store.scene(), savedAt: new Date().toISOString() };
    try {
      localStorage.setItem(DRAFT_KEY, JSON.stringify(draft));
      this.state.set('saved');
      this.message.set(null);
    } catch {
      this.state.set('quota');
      this.message.set('The browser has no room left for this draft. Sign in to save it to your account.');
    }
  }

  /** Painted bitmaps: an attachment for an owned project, a data URL inside a draft's scene. */
  private async savePaint(): Promise<void> {
    if (this.dirtyPaint.size === 0) return;
    const items = [...this.dirtyPaint];
    this.dirtyPaint.clear();
    for (const item of items) {
      const [layer, w] = item.split(':');
      const width = Number(w);
      const scene = this.store.scene();
      if (scene.layers[layer]?.kind !== 'paint') continue;
      const img = this.store.paint.pixels(scene, layer, width);
      if (this.store.mode() === 'owned') {
        const key = this.store.key()!;
        const name = `paint/${layer}/${width}.png`;
        try {
          const result = await this.api.putFile(key, name, await encodePng(img), 'image/png');
          this.usage.set(result.usage);
        } catch (e) {
          this.dirtyPaint.add(item);
          throw e;
        }
        if (this.store.scene().layers[layer]?.bitmaps[String(width)] !== name) this.store.silently(s => { s.layers[layer].bitmaps[String(width)] = name; });
      } else {
        const url = await toDataUrl(img);
        this.store.silently(s => { if (s.layers[layer]) s.layers[layer].bitmaps[String(width)] = url; });
      }
    }
  }

  private scheduleThumbnail(): void {
    if (this.thumbnailTimer) return;
    this.thumbnailTimer = setTimeout(() => {
      this.thumbnailTimer = null;
      void this.uploadThumbnail();
    }, 20_000);
  }

  private async uploadThumbnail(): Promise<void> {
    const key = this.store.key();
    if (this.store.mode() !== 'owned' || !key) return;
    try {
      const png = await thumbnailPng(this.store);
      await this.api.putFile(key, 'thumbnail.png', png, 'image/png');
    } catch {
      // A thumbnail is a nicety: the next save tries again.
    }
  }
}

/** A 128 px preview of the rest pose, for the project list. */
export async function thumbnailPng(store: EditorStore, size = 128): Promise<Uint8Array> {
  const scene = store.scene();
  const k = Math.min(size / scene.width, size / scene.height);
  const width = Math.max(1, Math.round(scene.width * k)), height = Math.max(1, Math.round(scene.height * k));
  const { positions } = evaluate(scene, { text: store.fonts });
  const img = renderToImageData(scene, { width, height, positions, paint: store.paint, text: store.fonts });
  return encodePng({ width: img.width, height: img.height, data: img.data });
}
