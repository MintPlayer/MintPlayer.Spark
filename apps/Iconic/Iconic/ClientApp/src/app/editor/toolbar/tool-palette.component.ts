import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { EditorStore, type ToolId } from '../editor-store';

interface ToolButton { id: ToolId; icon: string; label: string; key?: string }

const GROUPS: ToolButton[][] = [
  [
    { id: 'select', icon: 'cursor', label: 'Select and move', key: 'V' },
    { id: 'pan', icon: 'arrows-move', label: 'Pan', key: 'H' },
  ],
  [
    { id: 'point', icon: 'dot', label: 'Point', key: 'P' },
    { id: 'line', icon: 'slash-lg', label: 'Line', key: 'L' },
    { id: 'polyline', icon: 'activity', label: 'Polyline (Enter or double-click ends it)', key: 'Y' },
    { id: 'curve', icon: 'bezier2', label: 'Curve (drag to pull handles)', key: 'B' },
    { id: 'arc', icon: 'circle-half', label: 'Arc: centre, start, end', key: 'A' },
  ],
  [
    { id: 'rect', icon: 'square', label: 'Rectangle', key: 'R' },
    { id: 'circle', icon: 'circle', label: 'Circle: centre, then rim', key: 'C' },
    { id: 'ellipse', icon: 'egg', label: 'Ellipse', key: 'E' },
    { id: 'polygon', icon: 'pentagon', label: 'Polygon', key: 'G' },
    { id: 'closedCurve', icon: 'bezier', label: 'Closed curve' },
    { id: 'regular', icon: 'hexagon', label: 'Regular polygon' },
    { id: 'star', icon: 'star', label: 'Star' },
    { id: 'text', icon: 'fonts', label: 'Text', key: 'T' },
  ],
  [
    { id: 'pencil', icon: 'pencil', label: 'Pencil (paints pixels)', key: 'N' },
    { id: 'eraser', icon: 'eraser', label: 'Eraser', key: 'X' },
    { id: 'bucket', icon: 'paint-bucket', label: 'Fill', key: 'K' },
    { id: 'picker', icon: 'eyedropper', label: 'Pick colour', key: 'I' },
  ],
];

/** The tools (D16): drawing tools make shapes, paint tools paint pixels into a paint layer at an output's size. */
@Component({
  selector: 'app-tool-palette',
  imports: [BsButtonTypeDirective, SparkIconComponent],
  template: `
    @for (group of groups(); track $index) {
      <div class="group" role="group">
        @for (tool of group; track tool.id) {
          <button type="button" [color]="store.tool() === tool.id ? colors.primary : colors.light"
                  [title]="tool.label + (tool.key ? ' (' + tool.key + ')' : '')" [attr.aria-label]="tool.label"
                  [attr.aria-pressed]="store.tool() === tool.id" (click)="store.tool.set(tool.id)">
            <spark-icon [name]="tool.icon" />
          </button>
        }
      </div>
    }
  `,
  styles: `
    /* The gutter is reserved: the column is as wide as its content, so a scrollbar that appears on a short window
       would otherwise take its width out of the buttons and cut every icon in half. */
    :host { display: flex; flex-direction: column; gap: 8px; padding: 6px; overflow-y: auto; scrollbar-gutter: stable; }
    .group { display: flex; flex-direction: column; gap: 2px; }
    @media (max-width: 767.98px) {
      :host { flex-direction: row; overflow-x: auto; overflow-y: hidden; scrollbar-gutter: auto; }
      .group { flex-direction: row; }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToolPaletteComponent {
  protected readonly store = inject(EditorStore);
  protected readonly colors = Color;
  /** A cursor gets the hotspot tool; a shared drawing only pans. */
  protected readonly groups = computed<ToolButton[][]>(() => {
    if (this.store.readOnly()) return [[GROUPS[0][1]]];
    const groups = GROUPS.map(g => [...g]);
    if (this.store.kind() === 'Cursor') groups.push([{ id: 'hotspot', icon: 'crosshair', label: 'Cursor hotspot' }]);
    return groups;
  });
}
