import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { EntityAttributeDefinition, PersistentObject, QueryResultItem, SparkCellColumn } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkModerationService } from './spark-moderation.service';
import { ModerationVoteState } from './spark-moderation.models';

/**
 * The vote widget (#460): up, score, down. An attribute renderer — register it with
 * {@link sparkModerationRenderers} and give an attribute of an `IModeratable` type
 * `"renderer": "spark-vote"`. The attribute's own value is ignored: the score comes from
 * `/spark/moderation/votes` (batched per page), never from the document, so a vote never moves the
 * row's etag.
 *
 * An arrow the caller holds no right for (`canUpvote` / `canDownvote` false on the state) is
 * disabled, so a user without the privilege sees the score but is not offered a click that the server
 * refuses — except the active arrow, since withdrawing a vote needs no right.
 *
 * On the detail page the entity type is the object's. In a query grid a row carries no type, so the
 * column needs `"rendererOptions": { "type": "<entity type id or alias>" }`.
 */
@Component({
  selector: 'spark-vote',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (state(); as s) {
      <span class="spark-vote d-inline-flex align-items-center gap-1" [class.spark-vote-locked]="s.locked">
        <button type="button" class="btn btn-sm spark-vote-up" [class.btn-primary]="s.myVote === 1" [class.btn-outline-secondary]="s.myVote !== 1"
                [disabled]="busy() || s.locked || (s.canUpvote === false && s.myVote !== 1)" [attr.aria-pressed]="s.myVote === 1" [attr.aria-label]="'moderation.upvote' | t" (click)="cast(1)">▲</button>
        <span class="spark-vote-score fw-bold" [attr.aria-label]="'moderation.score' | t">{{ s.score }}</span>
        <button type="button" class="btn btn-sm spark-vote-down" [class.btn-danger]="s.myVote === -1" [class.btn-outline-secondary]="s.myVote !== -1"
                [disabled]="busy() || s.locked || (s.canDownvote === false && s.myVote !== -1)" [attr.aria-pressed]="s.myVote === -1" [attr.aria-label]="'moderation.downvote' | t" (click)="cast(-1)">▼</button>
        @if (error(); as message) {
          <span class="text-danger small spark-vote-error" role="alert">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class SparkVoteComponent {
  private readonly moderation = inject(SparkModerationService);

  // Renderer inputs (all optional: the hosts pass only what a renderer declares).
  value = input<unknown>();
  attribute = input<EntityAttributeDefinition | undefined>();
  column = input<SparkCellColumn | undefined>();
  options = input<Record<string, any> | undefined>();
  item = input<PersistentObject | QueryResultItem | Record<string, any> | undefined>();

  protected readonly state = signal<ModerationVoteState | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly target = computed(() => {
    const item = this.item() as Record<string, any> | undefined;
    const id = item?.['id'] as string | undefined;
    const type = (item?.['objectTypeId'] as string | undefined) ?? (this.options()?.['type'] as string | undefined);
    return id && type ? { id, type } : null;
  });

  constructor() {
    effect(() => {
      const target = this.target();
      if (!target) return;
      this.moderation.voteState(target.type, target.id).then(s => this.state.set(s));
    });
  }

  async cast(direction: 1 | -1): Promise<void> {
    const target = this.target();
    const current = this.state();
    if (!target || !current || this.busy()) return;
    // Clicking the active arrow withdraws the vote.
    const next = current.myVote === direction ? 0 : direction;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.state.set(await this.moderation.vote(target.type, target.id, next));
    } catch (e) {
      this.error.set(describeModerationError(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** A readable message for a refused moderation call (400 errors, 429 quota, 404 refusal). */
export function describeModerationError(e: unknown): string {
  const err = e as HttpErrorResponse;
  const body = err?.error as { result?: { errors?: { errorMessage?: unknown }[]; error?: string } } | undefined;
  const first = body?.result?.errors?.[0]?.errorMessage;
  if (typeof first === 'string') return first;
  if (first && typeof first === 'object') {
    const values = Object.values(first as Record<string, string>);
    if (values.length) return values[0];
  }
  return body?.result?.error ?? err?.message ?? 'Failed';
}
