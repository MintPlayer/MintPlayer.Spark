import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkConsentSettingsComponent } from '@mintplayer/ng-spark/consent';
import {
  refusalKey, refusalOf, SparkAccountOrder, SparkCheckoutAftercareComponent, SparkCommerceCheckoutService, SparkGuestOrdersInfo,
} from '@mintplayer/ng-spark/commerce/checkout';
import { SparkOrderInvoicesComponent } from '@mintplayer/ng-spark/commerce/invoicing';
import { SparkBackInStockView, SparkCommercePromotionsService } from '@mintplayer/ng-spark/commerce/promotions';
import { SparkShopAddressBookComponent } from '@mintplayer/ng-spark/commerce/shop';
import { SparkMyTicketsComponent } from '@mintplayer/ng-spark/ticketing/tickets';
import { injectStorefront } from '../storefront-config';
import { storefrontUrl } from '../storefront-paths';

/** The account area's sections, in page order; a host hides one by replacing the page. */
export type SparkAccountSection = 'orders' | 'guestOrders' | 'tickets' | 'addresses' | 'backInStock' | 'consent';

/** The sections the enabled features offer (pure, for the unit test). */
export function accountSections(config: { shop: boolean; ticketing: boolean }): SparkAccountSection[] {
  return [
    'orders',
    'guestOrders',
    ...(config.ticketing ? (['tickets'] as const) : []),
    ...(config.shop ? (['addresses', 'backInStock'] as const) : []),
    'consent',
  ];
}

/** The sign-in link: `{returnUrl}` in `pattern` becomes the escaped `returnUrl` (pure, for the unit test). */
export function signInHref(pattern: string | null | undefined, returnUrl: string): string | null {
  return pattern ? pattern.replace('{returnUrl}', encodeURIComponent(returnUrl)) : null;
}

/**
 * The customer account area (`/account`, GB10, PRD §4.14) for a signed-in buyer: the order history with each order's
 * invoices and credit notes and its withdrawal function and claims (GA1, GA8) on demand; attaching guest orders placed
 * with the account's confirmed e-mail address (D4); the tickets; the address book; back-in-stock subscriptions (GB11);
 * the consent settings (GA9). Signed out, it says so: the host's sign-in page is its own.
 */
@Component({
  selector: 'spark-storefront-account-page',
  imports: [
    TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent, SparkConsentSettingsComponent, SparkCheckoutAftercareComponent,
    SparkOrderInvoicesComponent, SparkShopAddressBookComponent, SparkMyTicketsComponent,
  ],
  templateUrl: './spark-storefront-account.page.html',
  styleUrl: './spark-storefront-account.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontAccountPage implements OnInit {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly promotions = inject(SparkCommercePromotionsService);
  private readonly lang = inject(SparkLanguageService);
  protected readonly config = injectStorefront();
  protected readonly sections = accountSections(this.config);
  protected readonly ticketPath = `/${this.config.paths.tickets}/{code}`;
  /** The host's sign-in page with `{returnUrl}` filled in; null when none is configured. */
  protected readonly signInLink = signInHref(this.config.signInUrl, storefrontUrl(this.config.paths.account));

  protected readonly signedIn = signal<boolean | null>(null);
  protected readonly orders = signal<SparkAccountOrder[]>([]);
  protected readonly total = signal(0);
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());
  protected readonly guest = signal<SparkGuestOrdersInfo | null>(null);
  protected readonly backInStock = signal<SparkBackInStockView[]>([]);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly status = signal('');
  protected readonly hasMore = computed(() => this.orders().length < this.total());

  async ngOnInit(): Promise<void> {
    const page = await this.checkout.accountOrders(0, 20).catch(() => null);
    this.signedIn.set(page !== null);
    if (!page) return;
    this.orders.set(page.orders);
    this.total.set(page.total);
    this.guest.set(await this.checkout.guestOrders().catch(() => null));
    if (this.config.shop) this.backInStock.set(await this.promotions.myBackInStock().catch(() => []));
  }

  protected orderUrl(order: SparkAccountOrder): string {
    return storefrontUrl(this.config.paths.checkout, 'orders', order.number);
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected toggle(number: string): void {
    const next = new Set(this.expanded());
    if (next.has(number)) next.delete(number);
    else next.add(number);
    this.expanded.set(next);
  }

  protected async more(): Promise<void> {
    const page = await this.checkout.accountOrders(this.orders().length, 20).catch(() => null);
    if (!page) return;
    this.orders.set([...this.orders(), ...page.orders]);
    this.total.set(page.total);
  }

  protected async attachGuestOrders(): Promise<void> {
    await this.run(async () => {
      const result = await this.checkout.attachGuestOrders();
      this.status.set(this.lang.t('commerceStorefront.account.attached').replace('{0}', String(result.numbers.length)));
      const page = await this.checkout.accountOrders(0, Math.max(20, this.orders().length));
      if (page) {
        this.orders.set(page.orders);
        this.total.set(page.total);
      }
      this.guest.set(await this.checkout.guestOrders());
    });
  }

  protected async unsubscribe(entry: SparkBackInStockView): Promise<void> {
    await this.run(async () => {
      await this.promotions.deleteBackInStock(entry.id);
      this.backInStock.set(this.backInStock().filter((e) => e.id !== entry.id));
      this.status.set(this.lang.t('commerceStorefront.account.backInStockRemoved'));
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    this.status.set('');
    try {
      await action();
    } catch (e) {
      this.refusal.set(refusalKey(refusalOf(e)?.code ?? 'conflict', this.lang));
    } finally {
      this.busy.set(false);
    }
  }
}
