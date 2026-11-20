import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsRangeComponent } from '@mintplayer/ng-bootstrap/range';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { keyedFrames, removePointKey, setMotionPath, setPointKey } from '../core/edit';
import { layerOpacity } from '../core/evaluate';
import type { Easing, Id } from '../core/model';
import { EditorStore } from '../editor-store';

const EASINGS: Easing[] = ['linear', 'easeIn', 'easeOut', 'easeInOut', 'step'];

/**
 * The animation (D5, D19): frames, speed and looping; a scrubber with the keyed frames marked; and, for the selected
 * points, a key at this frame (dragging a point in a frame keys it too), its easing, or a motion path along a guide.
 * "Rest pose" leaves the frames: edits there change the drawing itself.
 */
@Component({
  selector: 'app-timeline',
  imports: [FormsModule, BsButtonTypeDirective, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsRangeComponent, BsSelectComponent, BsSelectOption, SparkIconComponent],
  template: `
    <div class="bar">
      <button type="button" [color]="store.playing() ? colors.primary : colors.secondary" [attr.aria-label]="store.playing() ? 'Pause' : 'Play'" (click)="togglePlay()">
        <spark-icon [name]="store.playing() ? 'pause-fill' : 'play-fill'" />
      </button>
      <button type="button" [color]="store.frame() === null ? colors.primary : colors.light" (click)="rest()" title="Edit the drawing itself, not a frame">Rest pose</button>
      <div class="scrub">
        <bs-range [min]="0" [max]="frames() - 1" [step]="1" [label]="'Frame ' + ((store.frame() ?? 0) + 1) + ' of ' + frames()"
                  [value]="store.frame() ?? 0" (valueChange)="goTo($event)" />
        <div class="ticks" aria-hidden="true">
          @for (f of keyed(); track f) { <span class="tick" [style.left.%]="frames() > 1 ? (100 * f) / (frames() - 1) : 0"></span> }
        </div>
      </div>
      @if (!store.readOnly()) {
        <bs-form>
          <div class="d-flex gap-1 align-items-center">
            <label for="tl-frames" class="mb-0 small">Frames</label>
            <input id="tl-frames" class="num" type="number" min="1" max="600" [ngModel]="frames()" (ngModelChange)="setAnimation({ frames: clamp(+$event, 1, 600) })" [ngModelOptions]="{ updateOn: 'blur' }" />
            <label for="tl-fps" class="mb-0 small">fps</label>
            <input id="tl-fps" class="num" type="number" min="1" max="60" [ngModel]="store.scene().animation.fps" (ngModelChange)="setAnimation({ fps: clamp(+$event, 1, 60) })" [ngModelOptions]="{ updateOn: 'blur' }" />
          </div>
        </bs-form>
        <bs-checkbox [type]="'checkbox'" name="tl-loop" [checked]="store.scene().animation.loop" (checkedChange)="setAnimation({ loop: $event === true })">Loop</bs-checkbox>
      }
    </div>

    @if (!store.readOnly() && store.frame() !== null && !store.playing()) {
      <div class="bar mt-1">
        @if (points().length) {
          <span class="small">{{ points().length === 1 ? 'Point' : points().length + ' points' }}:</span>
          <button type="button" [color]="colors.secondary" (click)="keyHere()"><spark-icon name="key" /> Key here</button>
          @if (keyAtFrame(); as key) {
            <bs-select [ngModel]="key.easing" (ngModelChange)="setEasing($event)" aria-label="Easing into the next key">
              @for (e of easings; track e) { <option [ngValue]="e">{{ e }}</option> }
            </bs-select>
            <button type="button" [color]="colors.light" (click)="removeKey()"><spark-icon name="x-lg" /> Remove key</button>
          }
          @if (pathShapes().length) {
            <bs-select [ngModel]="null" (ngModelChange)="bindPath($event)" aria-label="Move along a shape">
              <option [ngValue]="null">Move along…</option>
              @for (s of pathShapes(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }
            </bs-select>
          }
        } @else {
          <span class="small text-body-secondary">Select points and drag them to key this frame, or select a point and a guide shape to move along it.</span>
        }
        @if (activeLayer(); as layer) {
          <span class="flex-grow-1"></span>
          <bs-range class="opacity" [min]="0" [max]="1" [step]="0.05" [label]="layer.name + ' opacity'" [value]="layer.opacity" (valueChange)="keyOpacity($event)" />
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; padding: 6px 8px; border-top: 1px solid var(--bs-border-color); background: var(--bs-body-bg); font-size: 0.875rem; }
    .bar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .scrub { position: relative; flex: 1 1 12rem; min-width: 10rem; }
    .ticks { position: absolute; left: 8px; right: 8px; bottom: -2px; height: 6px; pointer-events: none; }
    .tick { position: absolute; width: 6px; height: 6px; margin-left: -3px; border-radius: 50%; background: #fd7e14; }
    .num { width: 4.5rem; }
    .opacity { min-width: 10rem; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimelineComponent {
  protected readonly store = inject(EditorStore);
  protected readonly colors = Color;
  protected readonly easings = EASINGS;

  protected readonly frames = computed(() => Math.max(1, this.store.scene().animation.frames));
  protected readonly keyed = computed(() => keyedFrames(this.store.scene()));
  protected readonly points = computed(() => this.store.selectedPoints().filter(p => this.store.scene().points[p] && !this.store.scene().points[p].derive));
  protected readonly keyAtFrame = computed(() => {
    const frame = this.store.frame(), scene = this.store.scene();
    const first = this.points()[0];
    return first != null && frame != null ? scene.animation.tracks[first]?.keys.find(k => k.frame === frame) ?? null : null;
  });
  protected readonly pathShapes = computed(() => {
    const scene = this.store.scene();
    return this.store.selectedShapes().filter(id => scene.shapes[id]).map(id => ({ id, label: scene.shapes[id].name ?? `${scene.shapes[id].type} ${id.slice(-4)}` }));
  });
  protected readonly activeLayer = computed(() => {
    const id = this.store.activeLayer(), scene = this.store.scene(), frame = this.store.frame();
    if (!id || !scene.layers[id] || frame == null) return null;
    return { id, name: scene.layers[id].name, opacity: layerOpacity(scene, id, frame) };
  });

  private raf: number | null = null;
  private readonly fps = computed(() => this.store.scene().animation.fps);

  constructor() {
    // The frame follows the clock, not a chain of timer ticks: an interval restarted per frame drifted (26 of 30 fps
    // measured, because reading frame() here made every frame re-run this effect). Only playing and fps restart it.
    effect(() => {
      const playing = this.store.playing();
      const fps = this.fps();
      this.stop();
      if (!playing) return;
      const startFrame = untracked(() => this.store.frame()) ?? 0;
      let start: number | null = null;
      const tick = (now: number) => {
        start ??= now;
        const frames = untracked(() => this.frames());
        let next = startFrame + Math.floor((now - start) * fps / 1000);
        if (next >= frames) {
          if (!untracked(() => this.store.scene().animation.loop)) {
            this.store.frame.set(frames - 1);
            this.store.playing.set(false);
            return;
          }
          next %= frames;
        }
        if (next !== untracked(() => this.store.frame())) this.store.frame.set(next);
        this.raf = requestAnimationFrame(tick);
      };
      untracked(() => { if (this.store.frame() === null) this.store.frame.set(0); });
      this.raf = requestAnimationFrame(tick);
    });
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  private stop(): void {
    if (this.raf !== null) cancelAnimationFrame(this.raf);
    this.raf = null;
  }

  togglePlay(): void {
    this.store.playing.update(p => !p);
  }

  rest(): void {
    this.store.playing.set(false);
    this.store.frame.set(null);
  }

  goTo(frame: number): void {
    this.store.playing.set(false);
    this.store.frame.set(Math.round(frame));
  }

  setAnimation(patch: Partial<{ frames: number; fps: number; loop: boolean }>): void {
    this.store.change(s => { s.animation = { ...s.animation, ...patch }; });
    const frame = this.store.frame();
    if (frame != null && frame >= this.frames()) this.store.frame.set(this.frames() - 1);
  }

  keyHere(): void {
    const frame = this.store.frame();
    if (frame == null) return;
    const pos = this.store.evaluated().positions;
    this.store.change(s => { for (const p of this.points()) if (pos[p]) setPointKey(s, p, frame, pos[p]); });
  }

  removeKey(): void {
    const frame = this.store.frame();
    if (frame == null) return;
    this.store.change(s => { for (const p of this.points()) removePointKey(s, p, frame); });
  }

  setEasing(easing: Easing): void {
    const frame = this.store.frame();
    this.store.change(s => {
      for (const p of this.points()) { const k = s.animation.tracks[p]?.keys.find(x => x.frame === frame); if (k) k.easing = easing; }
    });
  }

  /** Puts the selected points on a shape for the whole animation; the shape becomes a guide (D5). */
  bindPath(shape: Id | null): void {
    if (!shape) return;
    this.store.change(s => { for (const p of this.points()) setMotionPath(s, p, shape); });
  }

  keyOpacity(value: number): void {
    const frame = this.store.frame(), layer = this.activeLayer();
    if (frame == null || !layer) return;
    this.store.change(s => {
      // The track multiplies the layer's own opacity: key the factor that gives what the slider shows.
      const base = s.layers[layer.id]?.opacity ?? 1;
      const factor = base > 0 ? Math.min(1, value / base) : 0;
      const track = (s.animation.layerTracks[layer.id] ??= { keys: [] });
      track.keys = track.keys.filter(k => k.frame !== frame);
      track.keys.push({ frame, value: factor, easing: 'easeInOut' });
      track.keys.sort((a, b) => a.frame - b.frame);
    });
  }

  protected clamp(v: number, min: number, max: number): number {
    return Number.isFinite(v) ? Math.max(min, Math.min(max, Math.round(v))) : min;
  }
}
