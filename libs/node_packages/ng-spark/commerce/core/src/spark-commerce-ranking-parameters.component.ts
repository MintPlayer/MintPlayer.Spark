import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceRankingParameter } from './generated/contracts';
import { SparkCommerceService } from './spark-commerce.service';

/**
 * The generated ranking-parameters page (commerce #521 M4, GA5; CRD Art. 6a(1)(a), P2B Art. 5): every
 * storefront list's main parameters in order of importance, from the server's registered sources, so
 * the page always matches the configured sort rules.
 */
@Component({
  selector: 'spark-commerce-ranking-parameters',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-commerce-ranking-parameters.component.html',
  styleUrl: './spark-commerce-ranking-parameters.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommerceRankingParametersComponent implements OnInit {
  private readonly commerce = inject(SparkCommerceService);

  protected readonly parameters = signal<CommerceRankingParameter[]>([]);
  protected readonly lists = computed(() => {
    const byList = new Map<string, CommerceRankingParameter[]>();
    for (const p of this.parameters()) byList.set(p.list, [...(byList.get(p.list) ?? []), p]);
    return [...byList.entries()].map(([list, items]) => ({ list, items: items.sort((a, b) => a.order - b.order) }));
  });

  async ngOnInit(): Promise<void> {
    this.parameters.set(await this.commerce.rankingParameters());
  }
}
