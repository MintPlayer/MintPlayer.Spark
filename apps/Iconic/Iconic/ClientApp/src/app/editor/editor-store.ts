import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { IconicApi, type ProjectKind } from '../api/iconic-api';
import { analyzeScene, evaluate, type Evaluated } from './core/evaluate';
import type { Vec } from './core/geometry';
import { History } from './core/history';
import { defaultPlaneFill, defaultStroke, emptyScene, layerOf, type FillStyle, type Id, type Scene, type StrokeStyle } from './core/model';
import { enabledOutputs } from './core/render';
import { DEFAULT_FONT, DEFAULT_FONT_URL, FontLibrary } from './core/text';
import { PaintStore } from './paint-store';
import { SceneSaver } from './scene-saver';

export type ToolId =
  | 'select' | 'pan' | 'point'
  | 'line' | 'polyline' | 'curve' | 'arc'
  | 'polygon' | 'closedCurve' | 'circle' | 'ellipse' | 'rect' | 'regular' | 'star'
  | 'text'
  | 'pencil' | 'eraser' | 'bucket' | 'picker'
  | 'hotspot';

/** Where the drawing lives: the owner's project, an anonymous browser draft (D3), or someone's share link (D20). */
export type EditorMode = 'owned' | 'draft' | 'shared';

type UndoEntry =
  | { kind: 'scene'; scene: Scene }
  | { kind: 'paint'; layer: Id; width: number; pixels: ImageData };

/**
 * One editor's state, provided by the editor page. The scene is immutable from the outside: every edit goes through
 * `change()`, which records undo and lets the saver diff it (S5). Positions are always derived (`evaluated`): the
 * stored points are the rest pose, and a frame or a drag is solved on top of them (D14, D19).
 */
@Injectable()
export class EditorStore {
  private readonly api = inject(IconicApi);

  readonly scene = signal<Scene>(emptyScene());
  readonly mode = signal<EditorMode>('draft');
  readonly readOnly = computed(() => this.mode() === 'shared');
  readonly key = signal<string | null>(null);
  readonly shareToken = signal<string | null>(null);
  readonly name = signal('Untitled');
  readonly kind = signal<ProjectKind>('Icon');

  readonly tool = signal<ToolId>('select');
  readonly selectedShapes = signal<Id[]>([]);
  readonly selectedPoints = signal<Id[]>([]);
  readonly activeLayerChoice = signal<Id | null>(null);
  readonly activeLayer = computed(() => {
    const s = this.scene(), chosen = this.activeLayerChoice();
    if (chosen && s.layers[chosen]) return chosen;
    return [...s.layerOrder].reverse().find(id => s.layers[id]?.kind === 'shape') ?? s.layerOrder[s.layerOrder.length - 1] ?? null;
  });

  /** New shapes take these; editing the style with nothing selected changes them. */
  readonly stroke = signal<StrokeStyle>(defaultStroke());
  /** Planes (closed shapes) fill by default; lines ignore it. */
  readonly fill = signal<FillStyle>(defaultPlaneFill());
  /** Sides of the regular-polygon tool, tips of the star tool. */
  readonly sides = signal(6);
  readonly paintColor = signal('#212529');
  readonly paintAlpha = signal(1);

  readonly gridEnabled = signal(true);
  readonly grid = signal(8);
  readonly snapEnabled = signal(true);
  readonly showGuides = signal(true);
  /** The canvas shows the design (null) or one output size rendered pixel for pixel (S4). */
  readonly previewWidth = signal<number | null>(null);

  /** Null is the rest pose; a frame number shows (and keys) that frame (D19). */
  readonly frame = signal<number | null>(null);
  readonly playing = signal(false);

  /** A drag in progress: solved live, written back on release. */
  readonly drag = signal<{ point: Id; target: Vec } | null>(null);
  /** A scene shown instead of the stored one while a gesture is in progress (moving shapes). */
  readonly live = signal<Scene | null>(null);
  readonly hold = signal<Id[]>([]);

  readonly fontsVersion = signal(0);
  readonly fonts = new FontLibrary(() => this.fontsVersion.update(v => v + 1));
  readonly paint = new PaintStore(name => this.fileUrl(name));

  readonly shown = computed(() => this.live() ?? this.scene());
  readonly evaluated = computed<Evaluated>(() => {
    this.fontsVersion();
    const drag = this.drag();
    return evaluate(this.shown(), { frame: this.frame(), drag: drag ?? undefined, hold: this.hold(), text: this.fonts });
  });
  readonly analysis = computed(() => analyzeScene(this.scene()));

  /** The output the paint tools paint into: the previewed size, else the largest enabled one. */
  readonly paintWidth = computed(() => {
    const preview = this.previewWidth();
    if (preview) return preview;
    const outputs = enabledOutputs(this.scene()).map(o => o.output.width);
    return outputs.length ? Math.max(...outputs) : this.scene().width;
  });

  private readonly history = new History<UndoEntry>();
  readonly canUndo = signal(false);
  readonly canRedo = signal(false);

  readonly saver = new SceneSaver(this, this.api);

  constructor() {
    this.fonts.load(DEFAULT_FONT, DEFAULT_FONT_URL);
    effect(() => {
      const scene = this.scene();
      untracked(() => {
        this.loadFonts(scene);
        this.saver.changed(scene);
      });
    });
  }

  /** Loads a scene without undo history (opening a project, restoring a snapshot). */
  load(scene: Scene): void {
    this.history.clear();
    this.syncHistory();
    this.selectedShapes.set([]);
    this.selectedPoints.set([]);
    this.live.set(null);
    this.drag.set(null);
    this.frame.set(null);
    this.paint.reset();
    this.scene.set(scene);
    this.paint.loadAll(scene);
  }

  /** An undoable edit on a copy of the scene. */
  change(edit: (draft: Scene) => void): void {
    if (this.readOnly()) return;
    const before = this.scene();
    const next = structuredClone(before);
    edit(next);
    this.history.push({ kind: 'scene', scene: before });
    this.scene.set(next);
    this.syncHistory();
  }

  /** An edit that is not a step of its own (a bitmap's attachment name after an upload). */
  silently(edit: (draft: Scene) => void): void {
    const next = structuredClone(this.scene());
    edit(next);
    this.scene.set(next);
  }

  /** Records a paint stroke for undo: the bitmap's pixels before it. */
  recordPaint(layer: Id, width: number, before: ImageData): void {
    this.history.push({ kind: 'paint', layer, width, pixels: before });
    this.syncHistory();
    this.saver.paintChanged(layer, width);
  }

  undo(): void {
    this.apply(this.history.undo(e => this.inverse(e)));
  }

  redo(): void {
    this.apply(this.history.redo(e => this.inverse(e)));
  }

  private inverse(entry: UndoEntry): UndoEntry {
    return entry.kind === 'scene'
      ? { kind: 'scene', scene: this.scene() }
      : { ...entry, pixels: this.paint.pixels(this.scene(), entry.layer, entry.width) };
  }

  private apply(entry: UndoEntry | null): void {
    if (!entry) return;
    if (entry.kind === 'scene') {
      this.scene.set(entry.scene);
      this.pruneSelection();
    } else {
      this.paint.putPixels(this.scene(), entry.layer, entry.width, entry.pixels);
      this.saver.paintChanged(entry.layer, entry.width);
    }
    this.syncHistory();
  }

  private syncHistory(): void {
    this.canUndo.set(this.history.canUndo);
    this.canRedo.set(this.history.canRedo);
  }

  pruneSelection(): void {
    const s = this.scene();
    this.selectedShapes.update(ids => ids.filter(id => s.shapes[id]));
    this.selectedPoints.update(ids => ids.filter(id => s.points[id]));
  }

  select(shapes: Id[], points: Id[] = []): void {
    this.selectedShapes.set(shapes);
    this.selectedPoints.set(points);
    const first = shapes[0];
    if (first) {
      const layer = layerOf(this.scene(), first);
      if (layer) this.activeLayerChoice.set(layer);
    }
  }

  clearSelection(): void {
    this.select([], []);
  }

  /** The URL of an attachment of the open drawing; data URLs pass through. */
  fileUrl(name: string): string | null {
    if (name.startsWith('data:')) return name;
    const key = this.key(), token = this.shareToken();
    if (this.mode() === 'owned' && key) return this.api.fileUrl(key, name);
    if (this.mode() === 'shared' && token) return this.api.sharedFileUrl(token, name);
    return null;
  }

  private loadFonts(scene: Scene): void {
    for (const [id, font] of Object.entries(scene.fonts)) {
      if (this.fonts.has(id)) continue;
      const url = this.fileUrl(font.file);
      if (url) this.fonts.load(id, url);
    }
  }
}
