import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Demonstrates a renderer on an AsDetail attribute (#241): `value` is the nested
 * PersistentObject, and `item` (#245) is the full Person being displayed.
 * Declares only the inputs it uses — the host filters the rest away.
 */
@Component({
  selector: 'app-address-card-detail-renderer',
  standalone: true,
  template: `
    @if (dict(); as a) {
      <div class="card" style="max-width: 24rem;">
        <div class="card-body py-2">
          <div>{{ a['Street'] }}</div>
          <div>{{ a['City'] }} {{ a['State'] }}</div>
          <small class="text-muted">Address of {{ itemName() }}</small>
        </div>
      </div>
    } @else {
      <span class="text-muted">-</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AddressCardDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<PersistentObject | null>();
  item = input<PersistentObject>();

  dict = computed(() => {
    const po = this.value();
    if (!po?.attributes?.length) return null;
    const d = Object.fromEntries(po.attributes.map(a => [a.name, a.value]));
    return d['Street'] || d['City'] || d['State'] ? d : null;
  });

  itemName = computed(() => {
    const attrs = this.item()?.attributes ?? [];
    return attrs.find(a => a.name === 'FirstName')?.value ?? this.item()?.name ?? '';
  });
}
