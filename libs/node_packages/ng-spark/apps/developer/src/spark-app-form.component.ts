import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SparkAppsTextPipe,
  permissionCovers,
  type SparkAppFields,
  type SparkWebhookCatalog,
} from '@mintplayer/ng-spark/apps/core';

/** What an App may do with one resource: nothing, read it, or read and write it (`write` covers `read`). */
export type SparkAppAccess = 'none' | 'read' | 'write';

/** The resources of the catalog's permissions (`reports` of `reports:read`), sorted. */
export function permissionResources(permissions: readonly string[]): string[] {
  return [...new Set(permissions.map(p => p.slice(0, p.lastIndexOf(':'))).filter(r => r))].sort();
}

/** The access `permissions` give to `resource`. */
export function accessOf(permissions: readonly string[], resource: string): SparkAppAccess {
  if (permissions.includes(`${resource}:write`)) return 'write';
  return permissions.includes(`${resource}:read`) ? 'read' : 'none';
}

/** `permissions` with `resource` set to `access`; the other resources keep theirs. */
export function withAccess(permissions: readonly string[], resource: string, access: SparkAppAccess): string[] {
  const others = permissions.filter(p => p !== `${resource}:read` && p !== `${resource}:write`);
  return access === 'none' ? others : [...others, `${resource}:${access}`];
}

const EMPTY: SparkAppFields = {
  slug: '', name: '', description: null, homepageUrl: null, webhookUrl: null, setupUrl: null, public: false, permissions: [], events: [],
};

/**
 * An App's settings (D5, D29): name and slug, links, its webhook, whether others may install it, what it may do
 * (per resource) and the events it receives. An event its permissions do not cover cannot be chosen; the server
 * refuses it as well.
 */
@Component({
  selector: 'spark-app-form',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsSelectComponent, BsSelectOption, BsSpinnerComponent, SparkAppsTextPipe],
  template: `
    <bs-form (submitted)="submit()">
      <div class="mb-3">
        <label for="sparkAppName" class="form-label">{{ 'app.name' | sparkApps }}</label>
        <input type="text" id="sparkAppName" name="name" required [ngModel]="fields().name" (ngModelChange)="patch({ name: $event })" />
      </div>
      <div class="mb-3">
        <label for="sparkAppSlug" class="form-label">{{ 'app.slug' | sparkApps }}</label>
        <input type="text" id="sparkAppSlug" name="slug" required pattern="[a-z0-9][a-z0-9-]*" [readonly]="!isNew()"
               [ngModel]="fields().slug" (ngModelChange)="patch({ slug: $event })" />
        <small class="text-muted">{{ 'app.slugHint' | sparkApps }}</small>
      </div>
      <div class="mb-3">
        <label for="sparkAppDescription" class="form-label">{{ 'app.description' | sparkApps }}</label>
        <input type="text" id="sparkAppDescription" name="description" [ngModel]="fields().description" (ngModelChange)="patch({ description: $event })" />
      </div>
      <div class="mb-3">
        <label for="sparkAppHomepage" class="form-label">{{ 'app.homepageUrl' | sparkApps }}</label>
        <input type="url" id="sparkAppHomepage" name="homepageUrl" [ngModel]="fields().homepageUrl" (ngModelChange)="patch({ homepageUrl: $event })" placeholder="https://" />
      </div>
      <div class="mb-3">
        <label for="sparkAppWebhook" class="form-label">{{ 'app.webhookUrl' | sparkApps }}</label>
        <input type="url" id="sparkAppWebhook" name="webhookUrl" [ngModel]="fields().webhookUrl" (ngModelChange)="patch({ webhookUrl: $event })" placeholder="https://" />
        <small class="text-muted">{{ 'app.webhookUrlHint' | sparkApps }}</small>
      </div>
      <div class="mb-3">
        <label for="sparkAppSetup" class="form-label">{{ 'app.setupUrl' | sparkApps }}</label>
        <input type="url" id="sparkAppSetup" name="setupUrl" [ngModel]="fields().setupUrl" (ngModelChange)="patch({ setupUrl: $event })" placeholder="https://" />
        <small class="text-muted">{{ 'app.setupUrlHint' | sparkApps }}</small>
      </div>
      <div class="mb-3">
        <bs-checkbox [type]="'checkbox'" [name]="'public'" [checked]="fields().public" (checkedChange)="patch({ public: $event === true })">
          {{ 'app.public' | sparkApps }}
        </bs-checkbox>
      </div>

      <fieldset class="mb-3">
        <legend class="form-label fs-6">{{ 'app.permissions' | sparkApps }}</legend>
        @for (resource of resources(); track resource) {
          <div class="d-flex align-items-center gap-2 mb-2 spark-app-permission" [attr.data-resource]="resource">
            <code class="flex-grow-1">{{ resource }}</code>
            <bs-select [title]="resource" [attr.aria-label]="resource" [ngModel]="access(resource)" (ngModelChange)="setAccess(resource, $event)"
                       [ngModelOptions]="{ standalone: true }">
              <option [ngValue]="'none'">{{ 'app.access.none' | sparkApps }}</option>
              <option [ngValue]="'read'">{{ 'app.access.read' | sparkApps }}</option>
              <option [ngValue]="'write'">{{ 'app.access.write' | sparkApps }}</option>
            </bs-select>
          </div>
        } @empty {
          <p class="text-muted">{{ 'app.noPermissions' | sparkApps }}</p>
        }
      </fieldset>

      <fieldset class="mb-3">
        <legend class="form-label fs-6">{{ 'app.events' | sparkApps }}</legend>
        @for (event of catalog().events; track event.name) {
          <bs-checkbox [type]="'checkbox'" [name]="'app-event-' + event.name" [checked]="fields().events.includes(event.name)"
                       [disabled]="!covered(event.permission)" (checkedChange)="toggle(event.name, $event === true)">
            <code>{{ event.name }}</code> <span class="text-muted">{{ event.summary }}</span>
            @if (!covered(event.permission)) { <small class="text-muted">({{ 'app.needs' | sparkApps: event.permission }})</small> }
          </bs-checkbox>
        }
      </fieldset>

      <button type="submit" class="btn btn-primary spark-app-save" [disabled]="busy() || !fields().name || !fields().slug">
        @if (busy()) { <bs-spinner class="me-1" /> }
        {{ (isNew() ? 'app.register' : 'save') | sparkApps }}
      </button>
    </bs-form>
  `,
})
export class SparkAppFormComponent {
  readonly catalog = input.required<SparkWebhookCatalog>();
  /** The App's current settings; empty for a new App. */
  readonly value = input<SparkAppFields | null>(null);
  readonly isNew = input(false);
  readonly busy = input(false);
  readonly saved = output<SparkAppFields>();

  readonly fields = signal<SparkAppFields>(EMPTY);
  protected readonly resources = computed(() => permissionResources([...this.catalog().permissions, ...this.fields().permissions]));

  constructor() {
    effect(() => this.fields.set({ ...EMPTY, ...(this.value() ?? {}) }));
  }

  patch(change: Partial<SparkAppFields>): void {
    this.fields.update(fields => ({ ...fields, ...change }));
  }

  protected access(resource: string): SparkAppAccess {
    return accessOf(this.fields().permissions, resource);
  }

  /** A narrower permission also drops the events it no longer covers. */
  setAccess(resource: string, access: SparkAppAccess): void {
    this.fields.update(fields => {
      const permissions = withAccess(fields.permissions, resource, access);
      const events = fields.events.filter(name => permissionCovers(permissions, this.permissionOf(name)));
      return { ...fields, permissions, events };
    });
  }

  protected covered(permission: string | null): boolean {
    return permissionCovers(this.fields().permissions, permission);
  }

  toggle(event: string, on: boolean): void {
    this.fields.update(fields => ({
      ...fields,
      events: on ? [...new Set([...fields.events, event])] : fields.events.filter(e => e !== event),
    }));
  }

  submit(): void {
    const fields = this.fields();
    // On a change the server reads null as "leave it" and "" as "clear it" (an empty webhook URL removes the
    // webhook), so a cleared field goes out as ""; a new App has nothing to clear.
    const optional = (value: string | null) => value?.trim() || (this.isNew() ? null : '');
    this.saved.emit({
      ...fields,
      slug: fields.slug.trim(),
      name: fields.name.trim(),
      description: optional(fields.description),
      homepageUrl: optional(fields.homepageUrl),
      webhookUrl: optional(fields.webhookUrl),
      setupUrl: optional(fields.setupUrl),
    });
  }

  private permissionOf(event: string): string | null {
    return this.catalog().events.find(e => e.name === event)?.permission ?? null;
  }
}
