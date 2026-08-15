import { reflectComponentType, Type } from '@angular/core';
import { PersistentObjectAttribute } from '@mintplayer/ng-spark/models';

// Reflection result cached per component type: the input builders are template
// expressions re-evaluated every CD pass (and query-list virtual-scrolls).
const declaredInputs = new Map<Type<any>, Set<string>>();

/**
 * Drops entries the component doesn't declare, so every contract member is
 * genuinely optional — NgComponentOutlet throws on an undeclared input.
 */
export function withDeclaredInputs(component: Type<any>, inputs: Record<string, any>): Record<string, any> {
  let declared = declaredInputs.get(component);
  if (!declared) {
    declared = new Set(reflectComponentType(component)?.inputs.map(i => i.templateName) ?? []);
    declaredInputs.set(component, declared);
  }
  const filtered: Record<string, any> = {};
  for (const key of Object.keys(inputs)) {
    if (declared.has(key)) filtered[key] = inputs[key];
  }
  return filtered;
}

/**
 * The renderer-facing value of an attribute: the flat value, or for AsDetail
 * attributes (whose flat value the server nulls on purpose) the nested
 * PersistentObject (single) / PersistentObject[] (array).
 */
export function rendererValue(attr: PersistentObjectAttribute | undefined): any {
  return attr?.value ?? attr?.object ?? attr?.objects;
}
