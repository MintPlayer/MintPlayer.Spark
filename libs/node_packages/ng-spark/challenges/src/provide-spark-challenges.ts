import { EnvironmentProviders, inject } from '@angular/core';
import { provideSparkClientMethods } from '@mintplayer/ng-spark/client-operations';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkChallengeOffer } from './generated/contracts';
import { SparkChallengeService } from './spark-challenge.service';

/** The client method a server step-up invokes (`ISparkHumanChallenges.StepUpAsync`, the 449 retry). */
export const SPARK_CHALLENGE_CLIENT_METHOD = 'spark.challenge';

/**
 * Registers the `spark.challenge` client method, so a Spark action can ask for a step-up challenge before a hold
 * (commerce #521 M16, research §6.10): the action answers 449, the browser solves the offer it carries, and the action
 * re-runs with the response. ALTCHA is solved without any UI; a hosted provider's widget opens in a native `<dialog>`
 * (K11), labelled and closable with Escape (which cancels the step-up: the server then refuses the action).
 *
 * ```ts
 * bootstrapApplication(App, { providers: [provideSparkChallenges()] });
 * ```
 */
export function provideSparkChallenges(): EnvironmentProviders {
  return provideSparkClientMethods({
    [SPARK_CHALLENGE_CLIENT_METHOD]: {
      invoke: (args: unknown) => {
        const challenges = inject(SparkChallengeService);
        const lang = inject(SparkLanguageService);
        const offer = args as SparkChallengeOffer;
        return offer.provider === 'altcha' ? challenges.solve(offer) : solveInDialog(challenges, offer, lang.t('challenges.title'));
      },
      supported: () => typeof globalThis.crypto?.subtle !== 'undefined',
    },
  });
}

async function solveInDialog(challenges: SparkChallengeService, offer: SparkChallengeOffer, title: string) {
  const dialog = document.createElement('dialog');
  dialog.className = 'spark-challenge-dialog';
  dialog.setAttribute('aria-label', title);
  const host = document.createElement('div');
  dialog.appendChild(host);
  document.body.appendChild(dialog);
  const abort = new AbortController();
  dialog.addEventListener('cancel', () => abort.abort());
  dialog.showModal();
  try {
    return await Promise.race([
      challenges.solve(offer, host, abort.signal),
      new Promise<never>((_, reject) => abort.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))),
    ]);
  } finally {
    dialog.close();
    dialog.remove();
  }
}
