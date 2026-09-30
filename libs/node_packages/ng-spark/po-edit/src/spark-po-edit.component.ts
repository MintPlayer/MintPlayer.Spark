import { ChangeDetectionStrategy, Component, LOCALE_ID, computed, inject, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, formatDate } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkPoFormComponent } from '@mintplayer/ng-spark/po-form';
import { TranslateKeyPipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import {
  EntityType,
  PersistentObject,
  PersistentObjectAttribute,
  ValidationError,
  ShowedOn,
  hasShowedOnFlag,
  nestedPoToDict,
  dictToNestedPo,
  EntityTypeResolver,
  isDateDataType,
  toDateInputValue,
  fromDateInputValue,
  wireDatesEqual,
  RefreshOverlay,
  applyOverlay,
} from '@mintplayer/ng-spark/models';
import { ConflictSide, MergeResult, MergeSchema, TheirChange, mergeThreeWay } from './conflict-merge';
import { SparkPoConflictDialogComponent } from './spark-po-conflict-dialog.component';
import { ReferenceLabels, addReferenceLabelsOf, addReferenceLabelsOfOptions } from './reference-labels';

/** A 409 whose merge found true conflicts: everything needed to re-run it with the user's choices. */
interface PendingConflict {
  /** The object as loaded, for its reference labels. */
  loaded: PersistentObject;
  theirs: PersistentObject;
  base: Record<string, any>;
  mine: Record<string, any>;
  theirsForm: Record<string, any>;
  schema: MergeSchema;
  result: MergeResult;
}

@Component({
  selector: 'spark-po-edit',
  imports: [CommonModule, BsAlertComponent, BsContainerComponent, BsSpinnerComponent, SparkPoFormComponent, SparkPoConflictDialogComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './spark-po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoEditComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly language = inject(SparkLanguageService);
  /** `SparkConfig.conflictDialog.showChangedBy`: whether the conflict notice may name a user at all. */
  private readonly showChangedBy = inject(SPARK_CONFIG, { optional: true })?.conflictDialog?.showChangedBy === true;

  saved = output<PersistentObject>();
  cancelled = output<void>();

  colors = Color;
  entityType = signal<EntityType | null>(null);
  item = signal<PersistentObject | null>(null);
  type = '';
  id = '';
  formData = signal<Record<string, any>>({});
  /**
   * Bound two-way to the form, which is where refreshes land. Read by
   * {@link getEditableAttributes} so the save sees the object the user was actually shown.
   */
  refreshOverlay = signal<RefreshOverlay>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);
  // Cached list of every entity type — needed by the AsDetail save path to resolve
  // nested type schemas when rebuilding the nested PO wire shape from the flat form dict.
  private allEntityTypes = signal<EntityType[]>([]);
  generalErrors = computed(() => this.validationErrors().filter(e => !e.attributeName));
  /** After a 409 was merged: what they changed, and that nothing has been saved yet. */
  private readonly conflictNoticeText = signal<string | null>(null);
  /** The notice with its "Changed by X at T", which fills in the name once History resolved it. */
  readonly conflictNotice = computed(() => {
    const text = this.conflictNoticeText();
    if (!text) return null;
    const item = this.item();
    const audit = item ? this.auditLine(item) : null;
    return audit ? `${text} ${audit}` : text;
  });
  /** A 409 whose merge has true conflicts, waiting on the dialog. */
  pendingConflict = signal<PendingConflict | null>(null);
  protected readonly pendingConflicts = computed(() => this.pendingConflict()?.result.conflicts ?? []);
  /**
   * The name History resolved for whoever wrote the version with `etag`, or null. Only ever set from
   * the revision list, so it is there only when `SparkConfig.conflictDialog.showChangedBy` is true and
   * the caller holds `History/T` (see {@link lookUpChangedBy}).
   */
  private readonly changedByName = signal<{ etag: string | undefined; name: string } | null>(null);
  private readonly form = viewChild(SparkPoFormComponent);
  private readonly locale = inject(LOCALE_ID);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type = params.get('type') || '';
    this.id = params.get('id') || '';

    try {
      const [types, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type, this.id)
      ]);

      const entityType = types.find(t => t.id === this.type || t.alias === this.type) || null;
      this.entityType.set(entityType);
      this.allEntityTypes.set(types);
      this.item.set(item);
      this.initFormData();
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.validationErrors.set([{
        attributeName: '',
        errorMessage: { en: error.error?.error || error.message || 'An unexpected error occurred' },
        ruleType: 'error'
      }]);
    }
  }

  initFormData(): void {
    this.formData.set(this.formDataFrom(this.item()));
  }

  /**
   * The form's flat-dict shape of `currentItem`. Also how a conflict merge brings the object as
   * loaded and as re-fetched into the form's shape, so all three sides went through the same
   * coercions and only real edits differ.
   */
  private formDataFrom(currentItem: PersistentObject | null): Record<string, any> {
    const data: Record<string, any> = {};
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = currentItem?.attributes.find(a => a.name === attr.name);
      if (attr.dataType === 'Reference') {
        data[attr.name] = itemAttr?.value ?? null;
      } else if (attr.dataType === 'AsDetail') {
        // Server emits nested PO(s) in attr.object / attr.objects. Flatten back into the
        // Record<string, any> shape the form has always used so the rest of the component
        // tree stays unchanged.
        if (attr.isArray) {
          data[attr.name] = (itemAttr?.objects ?? []).map(po => nestedPoToDict(po));
        } else {
          data[attr.name] = itemAttr?.object ? nestedPoToDict(itemAttr.object) : {};
        }
      } else if (attr.dataType === 'boolean' && !attr.lookupReferenceType) {
        // `?? false` only for a real two-state checkbox. A lookup-backed boolean has a third state
        // that is expressed as the absence of a value, and coercing it here destroyed that BEFORE
        // the control rendered: merely opening the form and saving turned "unset" into an explicit
        // false, permanently and with nothing shown to the user.
        data[attr.name] = itemAttr?.value ?? false;
      } else if (isDateDataType(attr.dataType)) {
        // The wire carries a full ISO-8601 instant with an offset; a native date/time control accepts
        // only a bare local wall clock. Assigning the wire value directly does not fail loudly -- the
        // control just renders blank, and saving the untouched form writes that blank back.
        data[attr.name] = toDateInputValue(attr.dataType, itemAttr?.value);
      } else {
        data[attr.name] = itemAttr?.value ?? '';
      }
    });
    return data;
  }

  /**
   * Resolves a nested AsDetail type by CLR name, for rebuilding the nested PO wire shape on save.
   *
   * ⚠️ <b>`detailTypes` first, catalogue second.</b> The catalogue from `/spark/types` is
   * <b>Query-gated</b>, and an AsDetail row type usually has no rights of its own — nobody grants
   * `Query/GateSettings`, because a gate is edited through the Repository that owns it. So the
   * catalogue does not contain it, the resolver returned undefined, and the save quietly took the
   * scalar branch: the attribute went out as a raw dict under `value` instead of a nested PO under
   * `object`, the server could not map it, and `EntityMapper`'s conversion `catch` swallowed the
   * failure. The save reported success and wrote nothing.
   * <para>
   * `detailTypes` is the parent's own copy of its row types, carried on the type definition for
   * exactly this reason (#385 fixed the rendering half; this is the save half). It is gated on the
   * parent's right, which is the right that governs editing the row anyway.
   * </para>
   */
  private resolveEntityType(): EntityTypeResolver {
    const cache = this.allEntityTypes();
    const detailTypes = this.entityType()?.detailTypes ?? [];
    return (clrName: string) =>
      detailTypes.find(t => t.clrType === clrName) ?? cache.find(t => t.clrType === clrName);
  }

  /**
   * The attributes this page will read values from when it builds the save.
   *
   * ⚠️ <b>Overlaid before filtering, not after loading.</b> A refresh hook can reveal an attribute
   * that was hidden when the object was loaded, and the ordinary reason it does so is that the
   * attribute has just become required. Filtering on the loaded state left such an attribute out of
   * both {@link initFormData} and the save payload, so the user filled in a field whose value was
   * then dropped on the floor and refused by the server as missing — with no way out of the form.
   * The overlay is the form's, bound two-way, so the two halves cannot drift again.
   */
  getEditableAttributes() {
    const overlay = this.refreshOverlay();
    return this.entityType()?.attributes
      .map(a => applyOverlay(a, overlay[a.name]))
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  async onSave(): Promise<void> {
    const currentItem = this.item();
    if (!this.entityType() || !currentItem) return;

    this.validationErrors.set([]);
    this.conflictNoticeText.set(null);
    this.isSaving.set(true);

    const resolver = this.resolveEntityType();
    const attributes: PersistentObjectAttribute[] = currentItem.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);

      // AsDetail: formData[name] is a flat dict (single) or array of dicts (array). Rebuild
      // the nested PO wire shape so the server's polymorphic converter hydrates it into a
      // PersistentObjectAttributeAsDetail.
      if (editableAttr?.dataType === 'AsDetail' && editableAttr.asDetailType) {
        const nestedType = resolver(editableAttr.asDetailType);
        if (nestedType) {
          const raw = this.formData()[attr.name];
          if (editableAttr.isArray) {
            const items: any[] = Array.isArray(raw) ? raw : [];
            return {
              ...attr,
              value: null,
              object: null,
              objects: items.map(item => dictToNestedPo((item ?? {}) as Record<string, any>, nestedType, resolver)),
              asDetailType: editableAttr.asDetailType,
              isValueChanged: true,
            };
          }
          return {
            ...attr,
            value: null,
            object: raw ? dictToNestedPo(raw as Record<string, any>, nestedType, resolver) : null,
            objects: null,
            asDetailType: editableAttr.asDetailType,
            isValueChanged: true,
          };
        }
      }

      // `in`, not a truthiness or `?? attr.value` check: an attribute the refresh revealed has no
      // slot until its control writes one, and an attribute the user cleared has a slot holding ''.
      // Coalescing would resurrect the loaded value on exactly the edit that removed it.
      const formData = this.formData();
      const rawValue = editableAttr && attr.name in formData ? formData[attr.name] : attr.value;

      if (editableAttr && isDateDataType(editableAttr.dataType)) {
        // Back out of the control's bare wall clock into a complete ISO-8601 instant, carrying the
        // viewer's offset for the entered date.
        const converted = fromDateInputValue(editableAttr.dataType, rawValue);
        // Compare by instant, not by text: the round trip rewrites the offset to the viewer's even
        // when nothing was edited.
        const changed = !wireDatesEqual(converted, attr.value);
        return {
          ...attr,
          // When the value did not actually change, send back exactly what was loaded. Sending the
          // re-converted string would preserve the instant but rewrite the stored offset to the
          // viewer's, so merely opening a record and saving it would relabel a Seattle registration
          // as a Brussels one. The offset is not business data (so the instant is what we guarantee),
          // but there is no reason to discard it on a save that changed nothing.
          value: changed ? converted : attr.value,
          isValueChanged: changed,
        };
      }

      const newValue = rawValue;
      return {
        ...attr,
        value: newValue,
        isValueChanged: editableAttr ? newValue !== attr.value : false
      };
    });

    const po: Partial<PersistentObject> = {
      id: currentItem.id,
      // Send back the token this object was loaded with. Left out, the server skips the
      // concurrency check entirely -- it is opt-in by presence -- and a save over somebody else's
      // edit succeeds silently. Left as undefined when the server sent none, which drops the key
      // from the JSON and restores exactly the old behaviour rather than sending an empty string
      // that could never match.
      etag: currentItem.etag,
      name: this.formData()['Name'] || currentItem.name,
      objectTypeId: this.entityType()!.id,
      attributes
    };

    try {
      const result = await this.sparkService.update(this.type, this.id, po);
      this.isSaving.set(false);
      this.saved.emit(result as PersistentObject);
      this.router.navigate(['/po', this.type, this.id]);
    } catch (e) {
      this.isSaving.set(false);
      const error = e as HttpErrorResponse;
      // ⚠️ The errors live under `result`, because every endpoint wraps its body in a
      // ClientOperationEnvelope: `{ result: { errors: [...] }, operations: [] }`. Reading
      // `error.error.errors` matched nothing, so this branch was dead and EVERY save-time
      // validation failure fell through to the raw Angular HTTP string below.
      const errors = error.error?.result?.errors ?? error.error?.errors;
      if (error.status === 400 && errors) {
        this.validationErrors.set(errors);
      } else if (error.status === 409) {
        // Somebody saved this record between the load and this save. The server's own body says
        // only "Concurrency conflict" -- deliberately, since the real message carries the change
        // vector -- which is accurate, untranslated, and tells the user nothing to do about it.
        // The form keeps its values, so the typing is not lost, and the message stays up until the
        // conflict is resolved (a failed re-fetch or a cancelled dialog leaves it there).
        this.validationErrors.set([{
          attributeName: '',
          errorMessage: { en: this.language.t('common.concurrencyConflict') },
          ruleType: 'error'
        }]);
        await this.resolveConcurrencyConflict();
      } else {
        this.validationErrors.set([{
          attributeName: '',
          // Prefer the server's own message, as the load path already does. A refusal arrives as
          // 404 by design (an anti-oracle measure), and `error.message` renders that as
          // "Http failure response for /spark/po/...: 404 Not Found" inside the validation summary.
          errorMessage: { en: error.error?.result?.error || error.error?.error || error.message || 'An unexpected error occurred' },
          ruleType: 'error'
        }]);
      }
    }
  }

  onCancel(): void {
    this.cancelled.emit();
    this.router.navigate(['/po', this.type, this.id]);
  }

  /**
   * After a 409: re-fetch the object (a normal read, so read rights and row security apply) and
   * merge this form onto it, three-way against the object as loaded (contributions PRD §5, Q10).
   * Disjoint edits merge silently and the page says what they changed; true conflicts open the
   * dialog. Either way the form is rebased onto the fresh etag and **nothing is saved** — the user
   * reviews and saves, and the server's validation and business rules run again on the result.
   */
  private async resolveConcurrencyConflict(): Promise<void> {
    const base = this.item();
    if (!base) return;
    let theirs: PersistentObject;
    try {
      theirs = await this.sparkService.get(this.type, this.id);
    } catch {
      return; // The concurrency message stays; there is nothing to merge against.
    }
    // Not awaited: the merge and the dialog never wait on, or fail because of, the name.
    // Opt-in: without showChangedBy no History request is made at all.
    this.changedByName.set(null);
    if (this.showChangedBy) void this.lookUpChangedBy(theirs);

    const attributes = this.getEditableAttributes();
    const baseForm = this.formDataFrom(base);
    const theirsForm = this.formDataFrom(theirs);
    const mine = structuredClone(this.formData());
    const schema: MergeSchema = { attributes, resolve: this.resolveEntityType() };
    const result = mergeThreeWay(baseForm, mine, theirsForm, schema);

    if (result.conflicts.length === 0) {
      this.rebase(theirs, result.merged, result.theirChanges, 'common.conflictMerged');
      return;
    }
    this.pendingConflict.set({ loaded: base, theirs, base: baseForm, mine, theirsForm, schema, result });
  }

  /**
   * Reference labels for the dialog, by id. The form keeps only ids; the labels are on the two reads
   * (theirs first: it is the fresher) and, for a reference the user just picked, in the candidate
   * lists the form's pickers choose from — top-level and per AsDetail column.
   */
  protected readonly conflictReferenceLabels = computed<ReferenceLabels>(() => {
    const pending = this.pendingConflict();
    if (!pending) return {};
    const labels: ReferenceLabels = {};
    addReferenceLabelsOf(pending.theirs, labels);
    addReferenceLabelsOf(pending.loaded, labels);
    const form = this.form();
    if (form) {
      addReferenceLabelsOfOptions(Object.values(form.referenceOptions()), labels);
      for (const columns of Object.values(form.asDetailReferenceOptions())) {
        addReferenceLabelsOfOptions(Object.values(columns), labels);
      }
    }
    return labels;
  });

  /**
   * Resolves who wrote their version to a display name, through History: the newest revision from
   * `POST /spark/po/revisions` (page size 1) carries the name the app's `IHistoryUserNameResolver`
   * gave it. Taken only when that revision is the version the merge ran against, and — when the
   * object has `ModifiedBy` — written by that same user.
   *
   * Rights: asked only with `History/T` (the endpoint refuses anyone else anyway), so a caller who may
   * not read the history never sees a name from it. Without History installed, or on any failure,
   * nothing is set and the line names no user. Called only when `SparkConfig.conflictDialog.showChangedBy`
   * is true.
   */
  private async lookUpChangedBy(theirs: PersistentObject): Promise<void> {
    this.changedByName.set(null);
    const typeId = this.entityType()?.id;
    if (!typeId) return;
    try {
      const permissions = await this.sparkService.getPermissions(typeId);
      if (permissions?.canViewHistory !== true) return;
      const revisions = await this.sparkService.postEnvelope<ChangedByRevision[]>('/po/revisions', { objectTypeId: typeId, id: this.id, take: 1 });
      const latest = Array.isArray(revisions) ? revisions[0] : undefined;
      if (!latest || typeof latest.userName !== 'string' || latest.userName === '') return;
      if (latest.isCurrent !== true || (theirs.etag && latest.changeVector !== theirs.etag)) return;
      const by = modifiedBy(theirs);
      if (by && latest.userId && latest.userId !== by) return;
      this.changedByName.set({ etag: theirs.etag, name: latest.userName });
    } catch {
      // No name: the conflict flow goes on without it.
    }
  }

  /** The dialog's choices, applied on top of the merge. */
  onConflictResolved(choices: Record<string, ConflictSide>): void {
    const pending = this.pendingConflict();
    if (!pending) return;
    const result = mergeThreeWay(pending.base, pending.mine, pending.theirsForm, pending.schema, choices);
    this.pendingConflict.set(null);
    this.rebase(pending.theirs, result.merged, result.theirChanges, 'common.conflictResolved');
  }

  /** Closing the dialog changes nothing: the form keeps its values and the conflict message. */
  onConflictCancelled(): void {
    this.pendingConflict.set(null);
  }

  /**
   * Makes `theirs` the object this page edits: its etag is what the next save sends, and it is the
   * base a second conflict is merged against. `isValueChanged` is computed against it on save, so
   * only what differs from their version is sent as changed.
   */
  private rebase(theirs: PersistentObject, merged: Record<string, any>, theirChanges: TheirChange[], noticeKey: string): void {
    this.item.set(theirs);
    this.formData.set(structuredClone(merged));
    this.validationErrors.set([]);

    const fields = [...new Map(theirChanges.map(c => [c.rootAttribute.name, c.rootAttribute])).values()]
      .map(a => this.language.resolve(a.label) || a.name);
    const text = fields.length > 0
      ? this.language.t(noticeKey).replace('{fields}', fields.join(', '))
      : this.language.t(`${noticeKey}NoFields`);
    this.conflictNoticeText.set(text);
  }

  /** The dialog's "they also changed" line, or null when the merge took nothing of theirs. */
  protected readonly theyChanged = computed(() => {
    const pending = this.pendingConflict();
    if (!pending || pending.result.theirChanges.length === 0) return null;
    const fields = [...new Map(pending.result.theirChanges.map(c => [c.rootAttribute.name, c.rootAttribute])).values()]
      .map(a => this.language.resolve(a.label) || a.name);
    return this.language.t('common.conflictTheyChanged').replace('{fields}', fields.join(', '));
  });

  protected readonly conflictAudit = computed(() => {
    const pending = this.pendingConflict();
    return pending ? this.auditLine(pending.theirs) : null;
  });

  /**
   * "Changed by X at T" for an `IAuditable` target. History stamps `ModifiedBy`/`ModifiedAt` on the
   * entity, and they reach this page only when the model declares them as attributes. `ModifiedBy` is
   * a user id (History stores ids, never names), and that id is never shown. X is the name History
   * resolved for exactly this version ({@link lookUpChangedBy}), which is only looked up when
   * `SparkConfig.conflictDialog.showChangedBy` is true; with no resolved name there is no user part.
   * Either half is shown alone when only it is present, and nothing when neither is.
   */
  private auditLine(po: PersistentObject): string | null {
    const resolved = this.changedByName();
    const at = po.attributes.find(a => a.name === 'ModifiedAt')?.value;
    const user = this.showChangedBy && resolved && resolved.etag === po.etag ? resolved.name : null;
    let time: string | null = null;
    if (at) {
      try { time = formatDate(at, 'medium', this.locale); } catch { time = String(at); }
    }
    if (user && time) return this.language.t('common.conflictChangedByAt').replace('{user}', user).replace('{time}', time);
    if (user) return this.language.t('common.conflictChangedBy').replace('{user}', user);
    if (time) return this.language.t('common.conflictChangedAt').replace('{time}', time);
    return null;
  }
}

/** The fields of a `/spark/po/revisions` row this page reads (`SparkRevision` in the History entry point). */
interface ChangedByRevision {
  changeVector?: string;
  userId?: string | null;
  userName?: string | null;
  isCurrent?: boolean;
}

/** The `ModifiedBy` user id on an audited object, or null. */
function modifiedBy(po: PersistentObject): string | null {
  const by = po.attributes.find(a => a.name === 'ModifiedBy')?.value;
  return typeof by === 'string' && by !== '' ? by : null;
}
