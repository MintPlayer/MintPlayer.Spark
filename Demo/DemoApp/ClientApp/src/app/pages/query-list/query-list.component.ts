import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsDatatableModule, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, SparkQuery } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-query-list',
  standalone: true,
  imports: [CommonModule, RouterModule, BsDatatableModule],
  templateUrl: './query-list.component.html',
  styleUrl: './query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class QueryListComponent implements OnInit {
  query: SparkQuery | null = null;
  entityType: EntityType | null = null;
  items: PersistentObject[] = [];
  settings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private sparkService: SparkService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        const queryId = params.get('queryId');
        if (!queryId) return of(null);
        return this.sparkService.getQuery(queryId);
      }),
      switchMap(query => {
        if (!query) return of({ query: null, entityType: null });
        this.query = query;
        return forkJoin({
          query: of(query),
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => {
              const type = types.find(t => t.name === query.contextProperty ||
                t.clrType.endsWith(query.contextProperty.replace(/s$/, '')));
              return of(type || null);
            })
          )
        });
      })
    ).subscribe(result => {
      if (result?.entityType) {
        this.entityType = result.entityType;
        this.cdr.markForCheck();
        this.loadItems();
      }
    });
  }

  loadItems(): void {
    if (!this.entityType) return;
    this.sparkService.list(this.entityType.clrType).subscribe(items => {
      this.items = items;
      this.cdr.markForCheck();
    });
  }

  getVisibleAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  }

  getAttributeValue(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    return attr?.breadcrumb || attr?.value || '';
  }

  onRowClick(item: PersistentObject): void {
    if (this.entityType) {
      this.router.navigate(['/po', this.entityType.clrType, item.id]);
    }
  }

  onCreate(): void {
    if (this.entityType) {
      this.router.navigate(['/po', this.entityType.clrType, 'new']);
    }
  }
}
