import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkInvoiceKind, SparkInvoiceSummary } from './generated/contracts';
import { SparkCommerceInvoicingService } from './spark-commerce-invoicing.service';

/**
 * An order's documents (commerce #521 M10): every receipt, invoice and credit note with its PDF link. Renders the empty
 * state for a caller who may not see the order.
 *
 * `<spark-order-invoices [orderNumber]="order.number" />`
 */
@Component({
  selector: 'spark-order-invoices',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-order-invoices.component.html',
  styleUrl: './spark-order-invoices.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkOrderInvoicesComponent {
  private readonly invoicing = inject(SparkCommerceInvoicingService);

  /** The order's number. */
  orderNumber = input.required<string>();

  protected readonly documents = signal<SparkInvoiceSummary[] | null>(null);

  constructor() {
    effect(() => {
      const number = this.orderNumber();
      this.documents.set(null);
      void this.invoicing.documents(number).then((list) => this.documents.set(list), () => this.documents.set([]));
    });
  }

  /** The translation key of a document kind. */
  protected kindKey(kind: SparkInvoiceKind): string {
    switch (kind) {
      case 'receipt': return 'commerceInvoicing.receipt';
      case 'creditNote':
      case 'feeCreditNote': return 'commerceInvoicing.creditNote';
      default: return 'commerceInvoicing.invoice';
    }
  }

  /** The total with the browser's own number formatting (K11: `Intl.NumberFormat`). */
  protected amount(document: SparkInvoiceSummary): string {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency: document.currency }).format(document.total);
  }
}
