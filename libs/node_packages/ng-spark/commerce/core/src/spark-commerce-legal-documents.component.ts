import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { formatSparkDateTime } from '@mintplayer/ng-spark/date-time';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceLegalDocumentView } from './generated/contracts';
import { SparkCommerceService } from './spark-commerce.service';

/**
 * The legal documents in force (commerce #521 M4, GA2): a seller's own (falling back to the platform's
 * per kind) or the platform's, each with its version, effective date and PDF. With `documentId` it
 * shows that one version — the one an order's buyer accepted, even after newer versions.
 */
@Component({
  selector: 'spark-commerce-legal-documents',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-commerce-legal-documents.component.html',
  styleUrl: './spark-commerce-legal-documents.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommerceLegalDocumentsComponent {
  protected readonly commerce = inject(SparkCommerceService);
  private readonly lang = inject(SparkLanguageService);

  /** "Version 3, in force from …", dates through the core facility (K10). */
  protected versionText(doc: CommerceLegalDocumentView): string {
    return this.lang.t('commerce.legal.version')
      .replace('{0}', String(doc.version))
      .replace('{1}', formatSparkDateTime(doc.effectiveFrom, 'viewer', 'date', currentLanguage()));
  }

  /** The seller; null for the platform's documents. */
  organizationId = input<string | null>(null);

  /** One specific version instead of the documents in force. */
  documentId = input<string | null>(null);

  /** Only these kinds (`terms-of-sale`, …); empty for all. */
  kinds = input<string[]>([]);

  protected readonly documents = signal<CommerceLegalDocumentView[]>([]);

  constructor() {
    effect(() => {
      const organizationId = this.organizationId();
      const documentId = this.documentId();
      const kinds = this.kinds();
      const load = documentId
        ? this.commerce.legalDocument(documentId).then(d => (d ? [d] : []))
        : this.commerce.legalDocuments(organizationId);
      load.then(docs => this.documents.set(kinds.length ? docs.filter(d => kinds.includes(d.kind)) : docs));
    });
  }
}
