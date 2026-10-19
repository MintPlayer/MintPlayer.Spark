import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridColumnDirective, BsGridComponent, BsGridRowDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { EntityType } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkModerationService } from './spark-moderation.service';
import { ModerationCaseDetail, ModerationCaseSummary } from './spark-moderation.models';
import { describeModerationError } from './spark-vote.component';

/**
 * The review queue (#460): open cases on the left, the selected case's review surface on the right —
 * the voter→target matrix, the timeline against the posts' creation, the accounts side by side (age,
 * registration method, how many accounts share a network with it, reputation now and after reversing)
 * and the rule that fired — with the decisions the case allows. Needs `Review/Moderation`; merge and
 * suspend also `Suspend/Moderation` (the server refuses otherwise).
 *
 * Routed by {@link sparkModerationRoutes} at `moderation/review`; `?case=` selects a case.
 */
@Component({
  selector: 'spark-review-queue',
  imports: [
    BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective,
    BsListGroupComponent, BsListGroupItemComponent, BsTableComponent, RouterLink, TranslateKeyPipe, DatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <bs-grid stopFullWidthAt="never" class="d-block spark-review-queue">
      <h1 class="h3">{{ 'moderation.reviewQueue' | t }}</h1>
      @if (error(); as message) { <bs-alert [type]="colors.danger" [announce]="true" class="d-block">{{ message }}</bs-alert> }
      <div bsRow class="g-3">
        <div [md]="4">
          @if (cases().length) {
            <bs-list-group>
              @for (c of cases(); track c.id) {
                <bs-list-group-item [active]="selected()?.case?.id === c.id">
                  <button type="button" class="w-100 text-start border-0 bg-transparent p-0 text-reset spark-case" (click)="open(c.id)">
                    <strong>{{ c.kind }}</strong>
                    @if (c.flagCount) { <bs-badge class="ms-1 spark-case-flag-count" [type]="colors.warning">{{ c.flagCount }}</bs-badge> }
                    @if (targets()[c.id]?.title; as title) { <div class="small fw-semibold text-truncate spark-case-target">{{ title }}</div> }
                    <div class="small">{{ c.summary }}</div>
                    <div class="small opacity-75">{{ c.openedAtUtc | date: 'short' }}</div>
                  </button>
                </bs-list-group-item>
              }
            </bs-list-group>
          } @else {
            <p class="text-muted spark-no-cases">{{ 'moderation.noCases' | t }}</p>
          }
        </div>
        <div [md]="8">
          @if (selected(); as d) {
            <bs-card class="d-block spark-case-detail">
              <bs-card-header>{{ 'moderation.rule' | t }}: {{ d.case.kind }} — {{ d.case.summary }}</bs-card-header>
              <div class="p-3">
                @if (targets()[d.case.id]; as target) {
                  <p class="spark-case-target-link">
                    @if (target.link) { <a [routerLink]="target.link">{{ target.title ?? d.case.targetId }}</a> }
                    @else { {{ target.title ?? d.case.targetId }} }
                  </p>
                }
                @if (d.accounts.length) {
                  <h2 class="h6">{{ 'moderation.accounts' | t }}</h2>
                  <bs-table class="d-block spark-case-accounts">
                    <thead><tr><th></th><th>{{ 'moderation.ageDays' | t }}</th><th></th><th>{{ 'moderation.sharesNetwork' | t }}</th><th>{{ 'moderation.repBefore' | t }}</th><th>{{ 'moderation.repAfter' | t }}</th></tr></thead>
                    <tbody>
                      @for (a of d.accounts; track a.id) {
                        <tr><td>{{ a.id }}</td><td>{{ a.ageDays }}</td><td>{{ a.registrationMethod }}</td><td>{{ a.sharesNetworkWith }}</td><td>{{ a.reputationBefore }}</td><td>{{ a.reputationAfter }}</td></tr>
                      }
                    </tbody>
                  </bs-table>
                }
                @if (d.matrix.length) {
                  <h2 class="h6">{{ 'moderation.matrix' | t }}</h2>
                  <ul class="small spark-case-matrix">
                    @for (m of d.matrix; track m.voterId + m.authorId) { <li>{{ m.voterId }} → {{ m.authorId }}: {{ m.votes }}</li> }
                  </ul>
                }
                @if (d.timeline.length) {
                  <h2 class="h6">{{ 'moderation.timeline' | t }}</h2>
                  <bs-table class="d-block small spark-case-timeline">
                    <tbody>
                      @for (t of d.timeline; track t.voteId) {
                        <tr><td>{{ t.castAtUtc | date: 'short' }}</td><td>{{ t.voterId }} → {{ t.authorId }}</td><td>{{ t.direction > 0 ? '▲' : t.direction < 0 ? '▼' : '·' }}</td><td>{{ t.secondsAfterPost ?? '' }} s</td></tr>
                      }
                    </tbody>
                  </bs-table>
                }
                @if (d.flags.length) {
                  <ul class="small spark-case-flags">
                    @for (f of d.flags; track f.flaggerId) { <li>{{ f.reason }} <span class="text-muted">({{ f.raisedAtUtc | date: 'short' }})</span></li> }
                  </ul>
                }
                @if (d.case.status === 'open') {
                  <div class="d-flex flex-wrap gap-2">
                    @if (d.case.kind === 'flag') {
                      <button type="button" class="btn btn-sm btn-success spark-decide-uphold" [disabled]="busy()" (click)="decide('uphold')">{{ 'moderation.uphold' | t }}</button>
                      <button type="button" class="btn btn-sm btn-outline-secondary spark-decide-decline" [disabled]="busy()" (click)="decide('decline')">{{ 'moderation.decline' | t }}</button>
                    } @else {
                      <button type="button" class="btn btn-sm btn-outline-secondary spark-decide-dismiss" [disabled]="busy()" (click)="decide('dismiss')">{{ 'moderation.dismiss' | t }}</button>
                      <button type="button" class="btn btn-sm btn-warning spark-decide-reverse" [disabled]="busy()" (click)="decide('reverse')">{{ 'moderation.reverse' | t }}</button>
                      @for (a of d.case.accountIds; track a) {
                        <button type="button" class="btn btn-sm btn-outline-danger spark-decide-merge" [disabled]="busy()" (click)="decide('merge', a)">{{ 'moderation.merge' | t }}: {{ a }}</button>
                        <button type="button" class="btn btn-sm btn-danger spark-decide-suspend" [disabled]="busy()" (click)="decide('suspend', a)">{{ 'moderation.suspend' | t }}: {{ a }}</button>
                      }
                    }
                  </div>
                }
              </div>
            </bs-card>
          }
        </div>
      </div>
    </bs-grid>
  `,
})
export class SparkReviewQueueComponent implements OnInit {
  private readonly moderation = inject(SparkModerationService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly spark = inject(SparkService);

  protected readonly colors = Color;

  protected readonly cases = signal<ModerationCaseSummary[]>([]);
  /** Per case id: the target post's title and route, once resolved. */
  protected readonly targets = signal<Record<string, { title: string | null; link: string[] | null }>>({});
  protected readonly selected = signal<ModerationCaseDetail | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.load();
    const requested = this.route.snapshot.queryParamMap.get('case');
    if (requested) await this.open(requested);
  }

  async open(caseId: string): Promise<void> {
    await this.run(async () => {
      this.selected.set(await this.moderation.case(caseId));
      await this.router.navigate([], { relativeTo: this.route, queryParams: { case: caseId }, replaceUrl: true });
    });
  }

  async decide(decision: string, accountId?: string): Promise<void> {
    const current = this.selected();
    if (!current) return;
    await this.run(async () => {
      await this.moderation.decide(current.case.id, decision, accountId);
      this.selected.set(await this.moderation.case(current.case.id));
      await this.load();
    });
  }

  private async load(): Promise<void> {
    await this.run(async () => this.cases.set(await this.moderation.cases('open')));
    void this.resolveTargets(this.cases());
  }

  /**
   * Names each case's post: its title (the object's breadcrumb) and a link to it. A case carries only
   * the target's id and type id; without this the queue listed "flag · Spam" with no way to tell
   * which post it was about. A target the caller cannot open (purged, hidden) keeps just its id.
   */
  private async resolveTargets(cases: ModerationCaseSummary[]): Promise<void> {
    const pending = cases.filter(c => c.targetId && c.targetType && !(c.id in this.targets()));
    if (!pending.length) return;
    let types: EntityType[] = [];
    try {
      types = await this.spark.getEntityTypes();
    } catch {
      return;
    }
    await Promise.all(pending.map(async c => {
      // The type is stored by id (D28): a name changes with an application's rename, an id never does.
      const type = types.find(t => t.id === c.targetType);
      const route = type ? type.alias || type.id : null;
      let title: string | null = null;
      if (route) {
        try {
          const po = await this.spark.get(route, c.targetId!);
          title = po.breadcrumb || null;
        } catch {
          // Not visible to the caller (or gone): the id stays the label, without a link.
        }
      }
      const link = route && title ? ['/po', route, c.targetId!] : null;
      this.targets.update(t => ({ ...t, [c.id]: { title, link } }));
    }));
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      this.error.set(describeModerationError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
