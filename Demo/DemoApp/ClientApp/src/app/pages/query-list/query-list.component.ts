import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsDatatableModule, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, SparkQuery } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

interface PaginationData<T> {
  data: T[];
  count: number;
  perPage: number;
  page: number;
}

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
  paginationData: PaginationData<PersistentObject> | null = null;
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
        const singularName = this.singularize(query.contextProperty);
        return forkJoin({
          query: of(query),
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => {
              const type = types.find(t =>
                t.name === query.contextProperty ||
                t.name === singularName ||
                t.clrType.endsWith(singularName)
              );
              return of(type || null);
            })
          )
        });
      })
    ).subscribe(result => {
      if (result?.entityType) {
        this.entityType = result.entityType;
        this.settings = new DatatableSettings({
          perPage: { values: [10, 25, 50], selected: 10 },
          page: { values: [1], selected: 1 },
          sortProperty: this.query?.sortBy || '',
          sortDirection: this.query?.sortDirection === 'desc' ? 'descending' : 'ascending'
        });
        this.cdr.markForCheck();
        this.loadItems();
      }
    });
  }

  private singularize(plural: string): string {
    // Handle irregular plurals
    const irregulars: { [key: string]: string } = {
      'People': 'Person',
      'Children': 'Child',
      'Men': 'Men',
      'Women': 'Woman'
    };
    if (irregulars[plural]) return irregulars[plural];

    // Handle regular plurals
    if (plural.endsWith('ies')) {
      return plural.slice(0, -3) + 'y';
    }
    if (plural.endsWith('es')) {
      return plural.slice(0, -2);
    }
    if (plural.endsWith('s')) {
      return plural.slice(0, -1);
    }
    return plural;
  }

  loadItems(): void {
    if (!this.entityType) return;
    this.sparkService.list(this.entityType.id).subscribe(items => {
      this.paginationData = {
        data: items,
        count: items.length,
        perPage: this.settings.perPage.selected,
        page: this.settings.page.selected
      };
      // Update page values for pagination
      const totalPages = Math.ceil(items.length / this.settings.perPage.selected) || 1;
      this.settings.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);
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
      this.router.navigate(['/po', this.entityType.id, item.id]);
    }
  }

  onCreate(): void {
    if (this.entityType) {
      this.router.navigate(['/po', this.entityType.id, 'new']);
    }
  }
}
