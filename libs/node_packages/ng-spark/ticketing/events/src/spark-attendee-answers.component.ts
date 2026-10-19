import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkAttendeeLine, SparkAttendeeQuestion, SparkAttendeeQuestionnaire } from './generated/contracts';
import { SparkTicketingEventsService, ticketingRefusalKey, ticketingRefusalOf } from './spark-ticketing-events.service';

/** The answers being edited, per `{lineId}:{position}` and question key. */
type Draft = Record<string, Record<string, { value: string; consent: boolean }>>;

/**
 * Attendee questions (GB3, PRD §4.11) of the buyer's own order: one form per ticket position, shown on the order page
 * right after placing ("at checkout") and on the ticket page later, until the deadline before the date. A sensitive
 * question (health, dietary needs) shows its consent text with an unticked box; the answer is refused without it
 * (GDPR Art. 9(2)(a)). Server errors come back per question (`ticketingEvents.answers.{code}`).
 *
 * `<spark-attendee-answers [orderNumber]="number" />`
 */
@Component({
  selector: 'spark-attendee-answers',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-attendee-answers.component.html',
  styleUrl: './spark-attendee-answers.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkAttendeeAnswersComponent {
  private readonly events = inject(SparkTicketingEventsService);
  private readonly lang = inject(SparkLanguageService);

  /** The order number. */
  orderNumber = input.required<string>();

  protected readonly questionnaire = signal<SparkAttendeeQuestionnaire | null>(null);
  protected readonly draft = signal<Draft>({});
  protected readonly errors = signal<Record<string, Record<string, string>>>({});
  protected readonly saved = signal<Record<string, boolean>>({});
  protected readonly busy = signal<string | null>(null);
  protected readonly refusal = signal<string | null>(null);

  constructor() {
    effect(() => {
      const number = this.orderNumber();
      this.questionnaire.set(null);
      void this.events.attendeeQuestions(number).then((q) => this.load(q), () => this.questionnaire.set(null));
    });
  }

  protected key(line: SparkAttendeeLine, position: number): string {
    return `${line.lineId}:${position}`;
  }

  protected value(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion): string {
    return this.draft()[this.key(line, position)]?.[question.key]?.value ?? '';
  }

  protected consent(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion): boolean {
    return this.draft()[this.key(line, position)]?.[question.key]?.consent ?? false;
  }

  protected isChosen(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion, choice: string): boolean {
    return this.value(line, position, question).split(',').includes(choice);
  }

  protected error(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion): string | null {
    return this.errors()[this.key(line, position)]?.[question.key] ?? null;
  }

  protected set(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion, value: string, consent?: boolean): void {
    const key = this.key(line, position);
    this.draft.update((draft) => {
      const current = draft[key]?.[question.key] ?? { value: '', consent: false };
      return { ...draft, [key]: { ...draft[key], [question.key]: { value, consent: consent ?? current.consent } } };
    });
    this.saved.update((s) => ({ ...s, [key]: false }));
  }

  protected toggleChoice(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion, choice: string, checked: boolean): void {
    const chosen = new Set(this.value(line, position, question).split(',').filter((c) => c));
    if (checked) chosen.add(choice); else chosen.delete(choice);
    this.set(line, position, question, [...chosen].join(','));
  }

  protected setConsent(line: SparkAttendeeLine, position: number, question: SparkAttendeeQuestion, consent: boolean): void {
    this.set(line, position, question, this.value(line, position, question), consent);
  }

  protected async save(line: SparkAttendeeLine, position: number): Promise<void> {
    const key = this.key(line, position);
    const answers = Object.entries(this.draft()[key] ?? {}).map(([questionKey, a]) => ({ questionKey, value: a.value, consent: a.consent }));
    this.busy.set(key);
    this.refusal.set(null);
    this.errors.update((e) => ({ ...e, [key]: {} }));
    try {
      const questionnaire = await this.events.saveAnswers({
        orderNumber: this.orderNumber(), lineId: line.lineId, position, answers, language: this.lang.language(),
      });
      this.load(questionnaire);
      this.saved.update((s) => ({ ...s, [key]: true }));
    } catch (e) {
      const refusal = ticketingRefusalOf(e);
      if (refusal?.errors.length) {
        this.errors.update((all) => ({
          ...all,
          [key]: Object.fromEntries(refusal.errors.map((err) => [err.key, 'ticketingEvents.answers.' + err.code])),
        }));
      }
      this.refusal.set(ticketingRefusalKey(refusal?.code ?? 'answersRefused'));
    } finally {
      this.busy.set(null);
    }
  }

  private load(questionnaire: SparkAttendeeQuestionnaire | null): void {
    this.questionnaire.set(questionnaire);
    const draft: Draft = {};
    for (const line of questionnaire?.lines ?? []) {
      for (const position of line.positions) {
        draft[this.key(line, position.position)] = Object.fromEntries(
          position.answers.map((a) => [a.questionKey, { value: a.value, consent: a.consented }]));
      }
    }
    this.draft.set(draft);
  }
}
