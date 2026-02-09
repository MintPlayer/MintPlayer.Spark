export interface ProgramUnit {
  id: string;
  name: string;
  icon?: string;
  type: string;
  queryId?: string;
  persistentObjectId?: string;
  order: number;
}

export interface ProgramUnitGroup {
  id: string;
  name: string;
  icon?: string;
  order: number;
  programUnits: ProgramUnit[];
}

export interface ProgramUnitsConfiguration {
  programUnitGroups: ProgramUnitGroup[];
}
