import { ChangeDetectionStrategy, Component, effect, inject, input, model, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkServicePoint, SparkServicePointKind } from './generated/contracts';
import { SparkCommerceShippingService } from './spark-commerce-shipping.service';

/**
 * The checkout point picker (GB5): the service points and parcel lockers nearest the shipping address, as a list the
 * buyer picks one from. The chosen point's id travels on the order and the label. A map is the host's to add (the
 * points carry latitude and longitude).
 *
 * `<spark-service-point-picker [country]="address.country" [postalCode]="address.postalCode" carrier="bpost" [(selected)]="point" />`
 */
@Component({
  selector: 'spark-service-point-picker',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-service-point-picker.component.html',
  styleUrl: './spark-service-point-picker.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkServicePointPickerComponent {
  private readonly shipping = inject(SparkCommerceShippingService);

  /** ISO 3166-1 alpha-2 of the shipping address. */
  country = input.required<string>();

  /** The shipping address' postal code. */
  postalCode = input.required<string>();

  /** The carrier of the chosen method; the server's default when empty. */
  carrier = input<string>('');

  /** The street, which narrows the search where the carrier takes it. */
  street = input<string>('');

  /** The house number. */
  houseNumber = input<string>('');

  /** Only lockers, only staffed points, or both (empty). */
  kind = input<SparkServicePointKind | ''>('');

  /** How many points to show. */
  limit = input<number>(10);

  /** The chosen point (two-way). */
  selected = model<SparkServicePoint | null>(null);

  protected readonly points = signal<SparkServicePoint[] | null>(null);
  protected readonly error = signal(false);
  private search = 0;

  constructor() {
    effect(() => {
      const country = this.country();
      const postalCode = this.postalCode()?.trim();
      const carrier = this.carrier();
      const street = this.street();
      const houseNumber = this.houseNumber();
      const kind = this.kind();
      const limit = this.limit();
      const id = ++this.search;
      this.error.set(false);
      if (!country || !postalCode) {
        this.points.set([]);
        return;
      }
      this.points.set(null);
      void this.shipping.servicePoints({
        country,
        postalCode,
        carrier: carrier || undefined,
        street: street || undefined,
        houseNumber: houseNumber || undefined,
        kind: kind || undefined,
        limit,
      }).then(
        (points) => {
          // A slower answer to an older search never overwrites a newer one.
          if (id !== this.search) return;
          this.points.set(points);
          const current = this.selected();
          if (current && !points.some((p) => p.id === current.id)) this.selected.set(null);
        },
        () => {
          if (id !== this.search) return;
          this.points.set([]);
          this.error.set(true);
        });
    });
  }

  protected pick(point: SparkServicePoint): void {
    this.selected.set(point);
  }

  protected kindKey(point: SparkServicePoint): string {
    return point.kind === 'parcelLocker' ? 'commerceShipping.parcelLocker' : 'commerceShipping.pickupPoint';
  }

  protected distance(metres: number | null | undefined): string | null {
    if (metres === null || metres === undefined) return null;
    return metres < 1000
      ? new Intl.NumberFormat(undefined, { style: 'unit', unit: 'meter', maximumFractionDigits: 0 }).format(metres)
      : new Intl.NumberFormat(undefined, { style: 'unit', unit: 'kilometer', maximumFractionDigits: 1 }).format(metres / 1000);
  }
}
