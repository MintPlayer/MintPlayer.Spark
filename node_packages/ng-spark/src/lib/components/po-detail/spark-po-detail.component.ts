import { ChangeDetectionStrategy, Component, computed, ContentChildren, inject, input, output, QueryList, signal, TemplateRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { SparkService } from '../../services/spark.service';
import { SparkLanguageService } from '../../services/spark-language.service';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { AttributeValuePipe } from '../../pipes/attribute-value.pipe';
import { RawAttributeValuePipe } from '../../pipes/raw-attribute-value.pipe';
import { AsDetailColumnsPipe } from '../../pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../pipes/as-detail-cell-value.pipe';
import { ArrayValuePipe } from '../../pipes/array-value.pipe';
import { ReferenceLinkRoutePipe } from '../../pipes/reference-link-route.pipe';
import { SparkIconComponent } from '../icon/spark-icon.component';
import { SparkDetailFieldTemplateDirective, SparkDetailFieldTemplateContext } from '../../directives/spark-detail-field-template.directive';
import { CustomActionDefinition } from '../../models/custom-action';
import { EntityType, EntityAttributeDefinition, AttributeTab, AttributeGroup } from '../../models/entity-type';
import { LookupReference } from '../../models/lookup-reference';
import { PersistentObject } from '../../models/persistent-object';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-po-detail',
  imports: [CommonModule, NgTemplateOutlet, RouterModule, BsAlertComponent, BsButtonGroupComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsTableComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe, RawAttributeValuePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, ArrayValuePipe, ReferenceLinkRoutePipe],
  templateUrl: './spark-po-detail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);

  @ContentChildren(SparkDetailFieldTemplateDirective) detailFieldTemplates!: QueryList<SparkDetailFieldTemplateDirective>;

  showCustomActions = input(true);
  extraActionsTemplate = input<TemplateRef<void> | null>(null);
  extraContentTemplate = input<TemplateRef<{ $implicit: PersistentObject; entityType: EntityType }> | null>(null);

  edited = output<void>();
  deleted = output<void>();
  customActionExecuted = output<{ action: CustomActionDefinition; item: PersistentObject }>();

  colors = Color;
  errorMessage = signal<string | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  item = signal<PersistentObject | null>(null);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});
  type = '';
  id = '';
  canEdit = signal(false);
  canDelete = signal(false);
  customActions = signal<CustomActionDefinition[]>([]);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type = params.get('type') || '';
    this.id = params.get('id') || '';

    try {
      const [entityTypes, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type, this.id)
      ]);

      this.allEntityTypes.set(entityTypes);
      this.entityType.set(entityTypes.find(t => t.id === this.type || t.alias === this.type) || null);
      this.item.set(item);
      this.loadLookupReferenceOptions();
      this.loadAsDetailTypes();

      const et = this.entityType();
      if (et) {
        const [permissions, actions] = await Promise.all([
          this.sparkService.getPermissions(et.id),
          this.sparkService.getCustomActions(et.id)
        ]);
        this.canEdit.set(permissions.canEdit);
        this.canDelete.set(permissions.canDelete);
        this.customActions.set(actions.filter(a => a.showedOn === 'detail' || a.showedOn === 'both'));
      }
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
    }
  }

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  private static readonly DEFAULT_TAB: AttributeTab = { id: '__default__', name: 'default', order: 0 };
  private static readonly DEFAULT_GROUP: AttributeGroup = { id: '__default__', name: 'default', order: 0 };

  resolvedTabs = computed((): AttributeTab[] => {
    const et = this.entityType();
    if (et?.tabs && et.tabs.length > 0) {
      return [...et.tabs].sort((a, b) => a.order - b.order);
    }
    return [SparkPoDetailComponent.DEFAULT_TAB];
  });

  resolvedGroups = computed((): AttributeGroup[] => {
    const et = this.entityType();
    if (et?.groups && et.groups.length > 0) {
      return [...et.groups].sort((a, b) => a.order - b.order);
    }
    return [SparkPoDetailComponent.DEFAULT_GROUP];
  });

  showTabs = computed(() => this.resolvedTabs().length > 1);

  groupsForTab(tab: AttributeTab): AttributeGroup[] {
    const groups = this.resolvedGroups();
    if (tab.id === '__default__') return groups;
    return groups.filter(g => g.tab === tab.id || (!g.tab && tab === this.resolvedTabs()[0]));
  }

  attrsForGroup(group: AttributeGroup): EntityAttributeDefinition[] {
    const attrs = this.visibleAttributes();
    if (group.id === '__default__') {
      return attrs;
    }
    return attrs.filter(a => a.group === group.id || (!a.group && group === this.resolvedGroups()[0]));
  }

  getDetailFieldTemplate(attr: EntityAttributeDefinition): TemplateRef<SparkDetailFieldTemplateContext> | null {
    if (!this.detailFieldTemplates) return null;
    const byName = this.detailFieldTemplates.find(t => t.name() === attr.name);
    if (byName) return byName.template;
    const byType = this.detailFieldTemplates.find(t => t.name() === attr.dataType);
    if (byType) return byType.template;
    return null;
  }

  getDetailFieldContext(attr: EntityAttributeDefinition, item: PersistentObject): SparkDetailFieldTemplateContext {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return {
      $implicit: attr,
      item,
      value: itemAttr?.value
    };
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );
    this.lookupReferenceOptions.set(entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>));
  }

  private async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const types = this.allEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};

    for (const attr of asDetailAttrs) {
      const asDetailType = types.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          const refEntries = await Promise.all(
            refCols.map(async col => {
              const results = await this.sparkService.executeQueryByName(col.query!);
              return [col.name, results] as const;
            })
          );
          this.asDetailReferenceOptions.update(prev => ({
            ...prev,
            [attr.name]: refEntries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, PersistentObject[]>)
          }));
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    try {
      await this.sparkService.executeCustomAction(this.type, action.name, this.item() || undefined);
      this.customActionExecuted.emit({ action, item: this.item()! });
      if (action.refreshOnCompleted) {
        const item = await this.sparkService.get(this.type, this.id);
        this.item.set(item);
      }
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.errorMessage.set(err.error?.error || err.message || 'Action failed');
    }
  }

  onEdit(): void {
    this.edited.emit();
    this.router.navigate(['/po', this.type, this.id, 'edit']);
  }

  async onDelete(): Promise<void> {
    if (confirm(this.lang.t('confirmDelete'))) {
      await this.sparkService.delete(this.type, this.id);
      this.deleted.emit();
      this.router.navigate(['/']);
    }
  }

  onBack(): void {
    window.history.back();
  }
}
