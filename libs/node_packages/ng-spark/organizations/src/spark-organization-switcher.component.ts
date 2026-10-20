import { HttpClient } from '@angular/common/http';
import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** One organization the switcher offers (`GET /spark/organizations/current`). */
export interface SparkOrganizationChoice {
  id: string;
  name: string;
  isPersonal: boolean;
  /** The caller's built-in role in this organization, as the server writes `OrganizationRole`: 1 Viewer, 2 Member, 3 Admin, 4 Owner. */
  role: number;
  /** The host-declared custom roles the caller holds in this organization (e.g. `scanner`, `finance`). */
  customRoles: string[];
}

/** What `/spark/organizations/current` answers: the chosen organization (`null`: all of them) and the caller's organizations. */
export interface SparkCurrentOrganization {
  current: string | null;
  organizations: SparkOrganizationChoice[];
}

/** English fallbacks of the switcher's texts (`organizations.spa.*` in the organizations package's translations). */
export const SPARK_ORGANIZATION_SWITCHER_TEXTS: Record<string, string> = {
  switcher: 'Organization',
  allOrganizations: 'All organizations',
  personal: 'personal',
};

/**
 * The organization switcher for an app's top bar (`docs/seo_analyzer_PRD.md` Q-M11a): "All organizations" or one
 * of the caller's organizations. The server keeps the choice (a cookie it checks against the caller's memberships
 * on every request), lists of organization-owned rows show only the chosen organization's, and a new row whose
 * form names no organization lands in it. A detail page, a sub-query and the organization list are not narrowed.
 *
 * ```html
 * @if (auth.isAuthenticated()) { <spark-organization-switcher /> }
 * ```
 *
 * Renders nothing for a user in one organization (or none), so it can sit in the top bar unconditionally. A
 * choice reloads the page (`reloadOnChange`), so every grid, live list and cached page reads the new scope.
 */
@Component({
  selector: 'spark-organization-switcher',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BsSelectComponent, BsSelectOption],
  template: `
    @if (state(); as state) {
      @if (state.organizations.length > 1) {
        <bs-select class="spark-organization-switcher" [title]="t('switcher')" [attr.aria-label]="t('switcher')"
                   [ngModel]="state.current" (ngModelChange)="choose($event)" [disabled]="busy()">
          <option [ngValue]="null">{{ t('allOrganizations') }}</option>
          @for (organization of state.organizations; track organization.id) {
            <option [ngValue]="organization.id">{{ label(organization) }}</option>
          }
        </bs-select>
      }
    }
  `,
  styles: [`
    :host { display: inline-block; max-width: 14rem; min-width: 0; }
    /* A phone's topbar holds the language, this, the bell and the account buttons in 375 px. */
    @media (max-width: 575.98px) { :host { max-width: 6rem; min-width: 4.25rem; } }
  `],
})
export class SparkOrganizationSwitcherComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly language = inject(SparkLanguageService);
  private readonly document = inject(DOCUMENT);

  /** Reload the page after a choice, so every list reads the new scope. Default true. */
  readonly reloadOnChange = input(true);

  readonly state = signal<SparkCurrentOrganization | null>(null);
  readonly busy = signal(false);

  async ngOnInit(): Promise<void> {
    try {
      this.state.set(await firstValueFrom(this.http.get<SparkCurrentOrganization>('/spark/organizations/current')));
    } catch {
      this.state.set(null);   // signed out, or the app has no organizations: no switcher
    }
  }

  protected t(key: string): string {
    return this.language.t('organizations.spa.' + key, SPARK_ORGANIZATION_SWITCHER_TEXTS[key]);
  }

  protected label(organization: SparkOrganizationChoice): string {
    return organization.isPersonal ? `${organization.name} (${this.t('personal')})` : organization.name;
  }

  /** Reloads the page; a seam for tests. */
  protected reload(): void {
    this.document.defaultView?.location.reload();
  }

  async choose(organizationId: string | null): Promise<void> {
    if (organizationId === (this.state()?.current ?? null)) return;
    this.busy.set(true);
    try {
      this.state.set(await firstValueFrom(this.http.post<SparkCurrentOrganization>('/spark/organizations/current', { organizationId })));
      if (this.reloadOnChange()) this.reload();
    } catch {
      // The organization is gone or the caller left it: show what the server holds now.
      await this.ngOnInit();
    } finally {
      this.busy.set(false);
    }
  }
}
