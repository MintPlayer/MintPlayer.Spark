import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType, LookupReference, PersistentObject, isReservedAsDetailKey, nestedPoToDict, resolveTranslation, resolvedBreadcrumb } from '@mintplayer/ng-spark/models';
import { applyFieldTemplate } from './apply-field-template';

@Pipe({ name: 'attributeValue', standalone: true, pure: true })
export class AttributeValuePipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null, entityType: EntityType | null, lookupRefOptions: Record<string, LookupReference>, allEntityTypes: EntityType[]): any {
    if (!item) return '';
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    if (attr.breadcrumb) return attr.breadcrumb;

    const attrDef = entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail') {
      // Server emits nested PO(s) in attr.objects (array) / attr.object (single) — attr.value is null.
      if (attr.isArray) {
        const count = attr.objects?.length ?? 0;
        if (count === 0) return '';
        return `${count} item${count !== 1 ? 's' : ''}`;
      }
      if (attr.object) {
        // The server has already resolved this object's breadcrumb, and it can resolve templates
        // this side cannot. A type's breadcrumb may name a computed property that `[IgnoreProperty]`
        // keeps out of the model — HR's `Address.Crumb` is exactly that, `[Breadcrumb, IgnoreProperty]`
        // — so the template `{Crumb}` has no matching attribute here and never will. Recomputing it
        // client-side produced an empty string and fell through to "(object)" while the correct
        // "Voorbeeldstraat 1, 1000 Brussel" sat unread on `attr.object.breadcrumb`.
        //
        // One value on that property is NOT a breadcrumb: when the template renders blank the
        // server substitutes the CLR type name, and printing it reads as data — a `Build` whose
        // `Feedback.State` was unset showed the literal "BuildFeedback" (#384). Filtering it here
        // lets the fallbacks below run, which is what they are for.
        const resolved = resolvedBreadcrumb(attr.object.breadcrumb, attrDef.asDetailType);
        if (resolved) return resolved;
        return this.formatAsDetailValue(attrDef, nestedPoToDict(attr.object), allEntityTypes);
      }
    }

    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = lookupRefOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          return resolveTranslation(option.values) || option.key;
        }
      }
    }

    if (attrDef?.dataType === 'boolean') {
      return attr.value ?? null;
    }

    return attr.value ?? '';
  }

  private formatAsDetailValue(attrDef: EntityAttributeDefinition, value: Record<string, any>, allEntityTypes: EntityType[]): string {
    const asDetailType = allEntityTypes.find(t => t.clrType === attrDef.asDetailType);

    if (asDetailType?.breadcrumb) {
      const result = applyFieldTemplate(asDetailType.breadcrumb, value);
      if (result && result.trim()) return result;
    }

    // Last resort, reached when the type declares no breadcrumb or its template resolved to
    // nothing. Joining the scalar values is always more use to a reader than "(object)", which
    // named the failure rather than the row and looked identical for every unresolvable cell.
    // Reserved keys are skipped: `nestedPoToDict` stashes the row key and the row's own breadcrumb
    // in this same dict, and joining those prints a guid and a placeholder as though they were
    // fields. Previously masked -- this branch was only reachable when the server sent no
    // breadcrumb, which is exactly when there was nothing stashed to leak.
    return Object.entries(value)
      .filter(([k, v]) => !isReservedAsDetailKey(k) && v != null && typeof v !== 'object' && String(v).trim() !== '')
      .map(([, v]) => v)
      .join(', ');
  }
}
