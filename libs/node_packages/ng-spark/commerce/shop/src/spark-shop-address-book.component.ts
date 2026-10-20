import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkShopAddress, SparkShopAddressBookEntry } from './generated/contracts';
import { shopRefusalKey, shopRefusalOf, SparkCommerceShopService } from './spark-commerce-shop.service';

/** An empty address form. */
export function emptyShopAddress(country = 'BE'): SparkShopAddress {
  return { name: '', company: '', street: '', houseNumber: '', addition: '', postalCode: '', city: '', country, email: '', phone: '' };
}

/** One line of an address for a list: street and number, postal code and city, country. */
export function shopAddressLine(address: SparkShopAddress): string {
  const street = [address.street, address.houseNumber, address.addition].filter((p) => !!p && `${p}`.trim()).join(' ');
  return [address.name, street, `${address.postalCode} ${address.city}`.trim(), address.country].filter((p) => !!p && p.trim()).join(', ');
}

/**
 * The account area's address book (GB10, PRD §4.14): the saved addresses with their labels, the default one marked,
 * and a form to add or edit one. The delivery step offers them and fills itself with the default.
 *
 * `<spark-shop-address-book />`
 */
@Component({
  selector: 'spark-shop-address-book',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-shop-address-book.component.html',
  styleUrl: './spark-shop-address-book.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShopAddressBookComponent implements OnInit {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly lang = inject(SparkLanguageService);

  protected readonly entries = signal<SparkShopAddressBookEntry[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly status = signal('');

  /** The form: null while closed; `entryId` null for a new address. */
  protected readonly editing = signal<{ entryId: string | null; label: string; isDefault: boolean; address: SparkShopAddress } | null>(null);

  protected readonly line = shopAddressLine;

  async ngOnInit(): Promise<void> {
    try {
      this.entries.set((await this.shop.addressBook())?.entries ?? []);
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.loaded.set(true);
    }
  }

  protected add(): void {
    this.editing.set({ entryId: null, label: '', isDefault: this.entries().length === 0, address: emptyShopAddress() });
  }

  protected edit(entry: SparkShopAddressBookEntry): void {
    this.editing.set({ entryId: entry.entryId, label: entry.label ?? '', isDefault: entry.isDefault, address: { ...entry.address } });
  }

  protected cancel(): void {
    this.editing.set(null);
  }

  protected set(field: keyof SparkShopAddress, value: string): void {
    const form = this.editing();
    if (!form) return;
    this.editing.set({ ...form, address: { ...form.address, [field]: field === 'country' ? value.trim().toUpperCase() : value } });
  }

  protected setLabel(label: string): void {
    const form = this.editing();
    if (form) this.editing.set({ ...form, label });
  }

  protected setDefault(isDefault: boolean): void {
    const form = this.editing();
    if (form) this.editing.set({ ...form, isDefault });
  }

  protected async save(): Promise<void> {
    const form = this.editing();
    if (!form) return;
    await this.run(async () => {
      const book = await this.shop.saveAddress({ address: form.address, entryId: form.entryId, label: form.label || null, isDefault: form.isDefault });
      this.entries.set(book.entries);
      this.editing.set(null);
      this.status.set(this.lang.t('commerceShop.addressBook.saved'));
    });
  }

  protected async remove(entry: SparkShopAddressBookEntry): Promise<void> {
    await this.run(async () => {
      this.entries.set((await this.shop.deleteAddress(entry.entryId)).entries);
      this.status.set(this.lang.t('commerceShop.addressBook.removed'));
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    this.status.set('');
    try {
      await action();
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  private showRefusal(error: unknown): void {
    this.refusal.set(shopRefusalKey(shopRefusalOf(error)?.code ?? 'conflict', this.lang));
  }
}
