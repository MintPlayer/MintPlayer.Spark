import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_DETAIL_ACTIONS, SPARK_DETAIL_PANELS, SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkModerationService } from './spark-moderation.service';
import { SparkVoteComponent, describeModerationError } from './spark-vote.component';
import { SparkFlagButtonComponent } from './spark-flag-button.component';
import { SparkModeratorPanelComponent } from './spark-moderator-panel.component';
import { provideSparkModeration, sparkModerationRenderers, sparkModerationRoutes } from './provide-spark-moderation';
import { SparkReviewQueueComponent } from './spark-review-queue.component';

function detailContext(overrides: Partial<SparkDetailContext> = {}): SparkDetailContext {
  return {
    type: 'question',
    id: 'questions/1',
    item: { id: 'questions/1', objectTypeId: 'question', name: 'Question', attributes: [] } as any,
    entityType: { id: 't', name: 'Question' } as any,
    permissions: null,
    deleted: null,
    reload: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

const flush = () => new Promise(resolve => setTimeout(resolve, 0));

describe('moderation entry point (#460)', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    spark = { postEnvelope: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SparkService, useValue: spark },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
        provideSparkModeration(),
      ],
    });
  });

  it('registers Flag as a detail action, the moderator panel, the vote renderer and the review route', () => {
    expect(TestBed.inject(SPARK_DETAIL_ACTIONS).map(a => a.component)).toEqual([SparkFlagButtonComponent]);
    expect(TestBed.inject(SPARK_DETAIL_PANELS).map(p => p.component)).toEqual([SparkModeratorPanelComponent]);
    expect(sparkModerationRenderers).toEqual([{ name: 'spark-vote', detailComponent: SparkVoteComponent, columnComponent: SparkVoteComponent }]);
    expect(sparkModerationRoutes()).toEqual([{ path: 'moderation/review', component: SparkReviewQueueComponent }]);
  });

  it('batches vote states asked for in the same macrotask into one request per type', async () => {
    spark.postEnvelope.mockResolvedValue([
      { id: 'questions/1', score: 3, up: 3, down: 0, myVote: 1, locked: false },
      { id: 'questions/2', score: -1, up: 0, down: 1, myVote: 0, locked: false },
    ]);
    const service = TestBed.inject(SparkModerationService);

    const [one, two, hidden] = await Promise.all([
      service.voteState('question', 'questions/1'),
      service.voteState('question', 'questions/2'),
      service.voteState('question', 'questions/hidden'),
    ]);

    expect(spark.postEnvelope).toHaveBeenCalledTimes(1);
    expect(spark.postEnvelope).toHaveBeenCalledWith('/moderation/votes', { objectTypeId: 'question', ids: ['questions/1', 'questions/2', 'questions/hidden'] });
    expect(one?.score).toBe(3);
    expect(two?.myVote).toBe(0);
    expect(hidden).toBeNull();
  });

  it('the vote widget votes, and clicking the active arrow withdraws', async () => {
    spark.postEnvelope.mockImplementation((path: string, body: any) => Promise.resolve(path === '/moderation/votes'
      ? [{ id: 'questions/1', score: 0, up: 0, down: 0, myVote: 0, locked: false }]
      : { id: 'questions/1', score: body.direction, up: body.direction > 0 ? 1 : 0, down: 0, myVote: body.direction, locked: false }));
    const fixture = TestBed.createComponent(SparkVoteComponent);
    fixture.componentRef.setInput('item', { id: 'questions/1', objectTypeId: 'question' });
    fixture.detectChanges();
    await flush();
    await flush();
    fixture.detectChanges();

    const up = fixture.nativeElement.querySelector('.spark-vote-up') as HTMLButtonElement;
    up.click();
    await flush();
    fixture.detectChanges();
    expect(spark.postEnvelope).toHaveBeenLastCalledWith('/moderation/vote', { objectTypeId: 'question', id: 'questions/1', direction: 1 });
    expect(fixture.nativeElement.querySelector('.spark-vote-score').textContent.trim()).toBe('1');

    (fixture.nativeElement.querySelector('.spark-vote-up') as HTMLButtonElement).click();
    await flush();
    expect(spark.postEnvelope).toHaveBeenLastCalledWith('/moderation/vote', { objectTypeId: 'question', id: 'questions/1', direction: 0 });
  });

  it('a grid cell takes the type from rendererOptions', async () => {
    spark.postEnvelope.mockResolvedValue([{ id: 'questions/9', score: 5, up: 5, down: 0, myVote: 0, locked: false }]);
    const fixture = TestBed.createComponent(SparkVoteComponent);
    fixture.componentRef.setInput('item', { id: 'questions/9', values: [] });
    fixture.componentRef.setInput('options', { type: 'question' });
    fixture.detectChanges();
    await flush();
    await flush();
    expect(spark.postEnvelope).toHaveBeenCalledWith('/moderation/votes', { objectTypeId: 'question', ids: ['questions/9'] });
  });

  it('flags with the reason the user typed', async () => {
    spark.postEnvelope.mockResolvedValue(undefined);
    vi.spyOn(globalThis, 'prompt').mockReturnValue('spam');
    const fixture = TestBed.createComponent(SparkFlagButtonComponent);
    fixture.componentRef.setInput('context', detailContext());
    fixture.detectChanges();

    await fixture.componentInstance.flag();
    fixture.detectChanges();

    expect(spark.postEnvelope).toHaveBeenCalledWith('/moderation/flag', { objectTypeId: 'question', id: 'questions/1', reason: 'spam' });
    expect(fixture.nativeElement.querySelector('.spark-flag-done')).not.toBeNull();
  });

  it('the moderator panel stays hidden for a caller without moderator rights and shows Unlock on a locked post', async () => {
    spark.postEnvelope.mockResolvedValueOnce({ id: 'questions/1', locked: true, canLock: false, canReview: false, canSuspend: false, openFlags: 0 });
    const none = TestBed.createComponent(SparkModeratorPanelComponent);
    none.componentRef.setInput('context', detailContext());
    none.detectChanges();
    await flush();
    none.detectChanges();
    expect(none.nativeElement.querySelector('.spark-moderator-panel')).toBeNull();

    spark.postEnvelope.mockResolvedValueOnce({ id: 'questions/1', locked: true, canLock: true, canReview: true, canSuspend: false, openFlags: 2, openCaseId: 'ModerationCases/flag/questions/1' });
    const moderator = TestBed.createComponent(SparkModeratorPanelComponent);
    moderator.componentRef.setInput('context', detailContext());
    moderator.detectChanges();
    await flush();
    moderator.detectChanges();
    expect(moderator.nativeElement.querySelector('.spark-unlock')).not.toBeNull();
    expect(moderator.nativeElement.querySelector('.spark-open-flags').textContent).toContain('2');
  });

  it('describes a validation refusal, a quota and a plain error', () => {
    expect(describeModerationError({ error: { result: { errors: [{ errorMessage: { en: 'This post is locked.' } }] } } })).toBe('This post is locked.');
    expect(describeModerationError({ error: { result: { error: 'You have cast 30 votes today.', retryAfterSeconds: 60 } } })).toBe('You have cast 30 votes today.');
    expect(describeModerationError({ message: 'boom' })).toBe('boom');
  });
});
