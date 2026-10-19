import { ChangeDetectionStrategy, Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { currentLanguage, formatMoney, resolveTranslation } from '@mintplayer/ng-spark/models';
import { formatSparkDateTime } from '@mintplayer/ng-spark/date-time';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceSellerLabel, SparkCommerceService, SparkCommerceTraderLabelComponent } from '@mintplayer/ng-spark/commerce/core';
import { SparkCheckoutDocument, SparkCheckoutSummary, SparkPlaceOrderResult } from './generated/contracts';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';

/** Where the browser goes; a seam so tests do not navigate. */
export type SparkCheckoutNavigate = (url: string) => void;

/** A seller of the basket: its trader label once loaded, its id until then. */
interface SellerEntry {
  organizationId: string;
  label: CommerceSellerLabel | null;
}

/**
 * The last step before paying (commerce #521, GA2; CRD Art. 6 and 8(2)): the main characteristics of every line, the
 * price broken down, the extras and the total; the sellers; the documents in force, each required one with its own
 * checkbox; the age attestation; the guest's e-mail; the payment method; the donation and insurance offers; the
 * marketing soft opt-in; and the pay button whose text the server names ("Order with obligation to pay").
 *
 * Nothing is ever pre-ticked: not the terms, the age, the insurance, a donation or the opt-in. The pay button stays
 * disabled until every required document and the age attestation are ticked.
 *
 * `<spark-checkout-step (placed)="openOrder($event.number)" />`
 */
@Component({
  selector: 'spark-checkout-step',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkCommerceTraderLabelComponent],
  templateUrl: './spark-checkout-step.component.html',
  styleUrl: './spark-checkout-step.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCheckoutStepComponent implements OnInit {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly commerce = inject(SparkCommerceService);
  private readonly lang = inject(SparkLanguageService);

  /** How to leave for the payment provider; `location.assign` by default. */
  navigate = input<SparkCheckoutNavigate>((url) => window.location.assign(url));

  /** The route of the sellers' trader pages (passed to `<spark-commerce-trader-label>`). */
  traderPageRoute = input<string>('/commerce/traders');

  /** The order was placed without a provider redirect (or with a relative one): open its page. */
  readonly placed = output<SparkPlaceOrderResult>();

  protected readonly summary = signal<SparkCheckoutSummary | null>(null);
  protected readonly sellers = signal<SellerEntry[]>([]);
  protected readonly accepted = signal<ReadonlySet<string>>(new Set());
  protected readonly ageAttested = signal(false);
  protected readonly email = signal('');
  protected readonly name = signal('');
  protected readonly method = signal<string | null>(null);
  protected readonly donation = signal<string | null>(null);
  protected readonly insurance = signal(false);
  protected readonly optIn = signal(false);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  protected readonly documents = computed(() => this.summary()?.documents ?? []);
  protected readonly required = computed(() => this.documents().filter(d => d.mustAccept));
  protected readonly canPlace = computed(() => {
    const summary = this.summary();
    if (!summary || this.busy()) return false;
    const accepted = this.accepted();
    if (!this.required().every(d => accepted.has(d.id))) return false;
    if (summary.minimumAge != null && !this.ageAttested()) return false;
    if (summary.needsEmail && !this.email().trim()) return false;
    return true;
  });
  protected readonly total = computed(() => this.money(this.summary()?.total));
  protected readonly orderButtonText = computed(() => {
    const key = this.summary()?.orderButtonKey;
    return key ? this.lang.t(key) : '';
  });

  ngOnInit(): void {
    void this.load(true);
  }

  /** Loads the summary again; `initial` also resets what the buyer ticked. */
  async load(initial = false): Promise<void> {
    try {
      const summary = await this.checkout.summary();
      this.summary.set(summary);
      if (!summary) return;
      if (initial) {
        this.accepted.set(new Set());
        this.ageAttested.set(false);
        this.optIn.set(false);
        this.method.set(summary.methods.length === 1 ? summary.methods[0].id : null);
        this.loadSellers(summary);
      }
      // The choices already on the basket (made earlier by the buyer), never a default of ours.
      this.insurance.set(summary.extras.some(e => e.code === 'insurance'));
      const donation = summary.extras.find(e => e.code === 'donation');
      const amounts = summary.donation?.amounts ?? [];
      this.donation.set(!donation ? null
        : amounts.some(a => a === donation.amount.Amount) ? String(donation.amount.Amount)
        : 'roundup');
    } catch (e) {
      this.showRefusal(e);
    }
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected donationAmount(amount: number): string {
    const currency = this.summary()?.total.Currency;
    return currency ? formatMoney({ Amount: amount, Currency: currency }) : String(amount);
  }

  protected serviceDate(value: string | null | undefined): string {
    return formatSparkDateTime(value, 'viewer', 'short', currentLanguage());
  }

  protected documentTitle(doc: SparkCheckoutDocument): string {
    return resolveTranslation(doc.title) || this.lang.t('commerce.legal.kind.' + doc.kind);
  }

  protected acceptText(doc: SparkCheckoutDocument): string {
    return this.lang.t('commerceCheckout.acceptDocument')
      .replace('{0}', this.documentTitle(doc))
      .replace('{1}', String(doc.version));
  }

  protected pdfUrl(doc: SparkCheckoutDocument): string {
    return this.checkout.legalDocumentPdfUrl(doc.id);
  }

  protected format(key: string, value: string | number): string {
    return this.lang.t(key).replace('{0}', String(value));
  }

  protected toggleDocument(id: string, checked: boolean): void {
    const next = new Set(this.accepted());
    if (checked) next.add(id); else next.delete(id);
    this.accepted.set(next);
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected chooseDonation(choice: string | null): Promise<void> {
    this.donation.set(choice);
    return this.setChoice('donation', choice);
  }

  protected chooseInsurance(accept: boolean): Promise<void> {
    this.insurance.set(accept);
    return this.setChoice('insurance', accept ? 'accept' : null);
  }

  protected async place(): Promise<void> {
    const summary = this.summary();
    if (!summary || !this.canPlace()) return;
    this.busy.set(true);
    this.refusal.set(null);
    try {
      const accepted = this.accepted();
      const email = this.email().trim();
      const name = this.name().trim();
      const result = await this.checkout.place({
        acceptedDocumentIds: summary.documents.filter(d => accepted.has(d.id)).map(d => d.id),
        attestedMinimumAge: summary.minimumAge != null && this.ageAttested() ? summary.minimumAge : null,
        email: email || null,
        name: name || null,
        marketingOptIn: summary.softOptInOffered && this.optIn(),
        method: this.method(),
      });
      if (result.redirectUrl && /^https?:\/\//i.test(result.redirectUrl)) {
        this.navigate()(result.redirectUrl);
      } else {
        this.placed.emit(result);
      }
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  private async setChoice(key: string, value: string | null): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      await this.checkout.setChoices({ [key]: value });
      await this.load();
    } catch (e) {
      this.showRefusal(e);
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  private loadSellers(summary: SparkCheckoutSummary): void {
    const ids = [...new Set(summary.sellers.filter((s): s is string => !!s))];
    this.sellers.set(ids.map(organizationId => ({ organizationId, label: null })));
    for (const organizationId of ids) {
      void this.commerce.traderPage(organizationId).then(page => {
        if (!page) return;
        const label: CommerceSellerLabel = { organizationId: page.organizationId, name: page.name, traderStatus: page.traderStatus };
        this.sellers.update(list => list.map(s => (s.organizationId === organizationId ? { organizationId, label } : s)));
      }, () => undefined);
    }
  }

  private showRefusal(error: unknown): void {
    const refusal = refusalOf(error);
    this.refusal.set(refusalKey(refusal?.code ?? 'conflict'));
  }
}
