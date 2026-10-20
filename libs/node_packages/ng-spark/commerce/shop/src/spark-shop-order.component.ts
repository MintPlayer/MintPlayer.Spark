import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkShopOrder, SparkShopParcel, SparkShopShipment } from './generated/contracts';
import { SparkCommerceShopService } from './spark-commerce-shop.service';

/** The parcels of an order that have a tracking link, outbound first (the account area's "where is my order"). */
export function trackableParcels(orders: SparkShopOrder[]): { shipment: SparkShopShipment; parcel: SparkShopParcel }[] {
  return orders
    .flatMap((o) => o.shipments)
    .sort((a, b) => Number(a.return) - Number(b.return))
    .flatMap((shipment) => shipment.parcels.filter((p) => !!p.trackingUrl).map((parcel) => ({ shipment, parcel })));
}

/**
 * The shop's side of the buyer's order page (commerce #521 M17; the M12 service had only the `order()` call): per seller,
 * the delivery method or service point, each line with what has shipped, the shipments with their parcels and tracking
 * links (returns marked), the delivery promise, and the delivered date. Renders nothing for an order without goods, so
 * the storefront's order page can always include it.
 *
 * `<spark-shop-order [number]="orderNumber" />`
 */
@Component({
  selector: 'spark-shop-order',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-shop-order.component.html',
  styleUrl: './spark-shop-order.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShopOrderComponent {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly lang = inject(SparkLanguageService);

  /** The order number. */
  number = input.required<string>();

  protected readonly orders = signal<SparkShopOrder[]>([]);

  constructor() {
    effect(() => {
      const number = this.number();
      this.orders.set([]);
      void this.shop.order(number).then((orders) => this.orders.set(orders ?? []), () => this.orders.set([]));
    });
  }

  protected stateKey(state: string): string {
    return 'commerceShop.order.state.' + state;
  }

  protected trackText(parcel: SparkShopParcel): string {
    return this.lang.t('commerceShop.order.track').replace('{number}', parcel.trackingNumber ?? String(parcel.number));
  }

  protected shipmentKey(shipment: SparkShopShipment): string {
    return shipment.return ? 'commerceShop.order.returnShipment' : 'commerceShop.order.shipment';
  }
}
