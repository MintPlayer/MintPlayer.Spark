import { ChangeDetectionStrategy, Component, computed, inject, OnInit, output, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkServicePoint, SparkServicePointPickerComponent } from '@mintplayer/ng-spark/commerce/shipping';
import { SparkShopAddress, SparkShopAddressBookEntry, SparkShopDeliveryChoice, SparkShopDeliveryRate } from './generated/contracts';
import { shopRefusalKey, shopRefusalOf, SparkCommerceShopService } from './spark-commerce-shop.service';

/**
 * The checkout delivery step (commerce #521 M12; PRD §4.1 "the checkout shipping step lives in Shop"): the delivery
 * address, the methods every seller's parcel can be sent with (price, free-from threshold, delivery promise, customs and
 * DAP/DDP, GB6), a service point or locker where the method needs one (GB5), and the choice, which the basket then
 * prices as an order extra. Hidden when the basket holds no goods. Place it before Checkout's last step.
 *
 * `<spark-shop-delivery-step (chosen)="refreshSummary()" />`
 */
@Component({
  selector: 'spark-shop-delivery-step',
  imports: [TranslateKeyPipe, SparkServicePointPickerComponent],
  templateUrl: './spark-shop-delivery-step.component.html',
  styleUrl: './spark-shop-delivery-step.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShopDeliveryStepComponent implements OnInit {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly lang = inject(SparkLanguageService);

  /** The buyer chose (or changed) the delivery. */
  chosen = output<SparkShopDeliveryChoice>();

  protected readonly needsDelivery = signal(true);
  protected readonly address = signal<SparkShopAddress>({ name: '', street: '', houseNumber: '', postalCode: '', city: '', country: 'BE', email: '' });
  protected readonly rates = signal<SparkShopDeliveryRate[] | null>(null);
  protected readonly method = signal<string | null>(null);
  protected readonly point = signal<SparkServicePoint | null>(null);
  protected readonly choice = signal<SparkShopDeliveryChoice | null>(null);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  protected readonly rate = computed(() => this.rates()?.find((r) => r.method === this.method()) ?? null);
  protected readonly canChoose = computed(() => {
    const rate = this.rate();
    const a = this.address();
    return !!rate && !!a.name.trim() && !!a.street.trim() && !!a.postalCode.trim() && !!a.city.trim() && !!a.country.trim() && (!rate.servicePoint || !!this.point());
  });

  async ngOnInit(): Promise<void> {
    // The current choice, and whether the step applies at all.
    const options = await this.shop.deliveryOptions(this.address().country);
    this.needsDelivery.set(options.needsDelivery);
    if (options.chosen) {
      this.choice.set(options.chosen);
      this.address.set({ ...options.chosen.shipTo });
      this.method.set(options.chosen.method);
    }
    // GB10: a signed-in buyer's saved addresses; the default fills an empty step. Signed out answers null.
    if (options.needsDelivery) {
      const book = await this.shop.addressBook().catch(() => null);
      this.saved.set(book?.entries ?? []);
      const preferred = book?.entries.find((e) => e.isDefault);
      if (!options.chosen && preferred) this.address.set({ ...preferred.address });
    }
  }

  /** GB10: the address book's entries (empty when signed out). */
  protected readonly saved = signal<SparkShopAddressBookEntry[]>([]);

  /** Fills the form from a saved address; the rates are asked for again. */
  protected useSaved(entryId: string): void {
    const entry = this.saved().find((e) => e.entryId === entryId);
    if (!entry) return;
    this.address.set({ ...entry.address });
    this.rates.set(null);
  }

  protected set(field: keyof SparkShopAddress, value: string): void {
    this.address.set({ ...this.address(), [field]: field === 'country' ? value.trim().toUpperCase() : value });
  }

  protected async showRates(): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      const a = this.address();
      const options = await this.shop.deliveryOptions(a.country, a.postalCode);
      this.needsDelivery.set(options.needsDelivery);
      this.rates.set(options.rates);
      if (!options.rates.some((r) => r.method === this.method())) this.method.set(options.rates[0]?.method ?? null);
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  protected selectMethod(method: string): void {
    this.method.set(method);
    this.point.set(null);
  }

  protected async choose(): Promise<void> {
    const rate = this.rate();
    if (!rate || !this.canChoose()) return;
    this.busy.set(true);
    this.refusal.set(null);
    try {
      const point = rate.servicePoint ? this.point() : null;
      const choice = await this.shop.chooseDelivery({
        shipTo: this.address(),
        method: rate.method,
        servicePointId: point?.id ?? null,
        servicePointCarrier: point?.carrier ?? null,
      });
      this.choice.set(choice);
      this.chosen.emit(choice);
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  protected price(rate: SparkShopDeliveryRate): string {
    return rate.free ? this.lang.t('commerceShop.delivery.free') : formatMoney(rate.price);
  }

  protected text(key: string, name: string, value: string): string {
    return this.lang.t(key).replace(`{${name}}`, value);
  }

  /** A method's label: the host's translation `commerceShop.method.{key}`, else the configured key. */
  protected methodLabel(method: string): string {
    const key = 'commerceShop.method.' + method;
    const text = this.lang.t(key);
    return text === key ? method : text;
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  private showRefusal(error: unknown): void {
    this.refusal.set(shopRefusalKey(shopRefusalOf(error)?.code ?? 'conflict', this.lang));
  }
}
