import { SparkAltchaChallenge } from './generated/contracts';

/**
 * Solves an ALTCHA challenge (altcha-lib v1, SHA-256) with WebCrypto (K11: a standard API, no library): finds the
 * `number` with `hex(SHA-256(salt + number)) === challenge` and answers the base64 JSON payload the server verifies.
 *
 * Digests run in parallel batches and the loop yields between batches, so the page stays responsive; `signal` aborts.
 * The search space is `maxnumber` (the server's complexity); on average half of it is tried.
 */
export async function solveAltcha(challenge: SparkAltchaChallenge, signal?: AbortSignal, batch = 256): Promise<string> {
  if (challenge.algorithm.toUpperCase() !== 'SHA-256') throw new Error(`Unsupported ALTCHA algorithm ${challenge.algorithm}.`);
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) throw new Error('WebCrypto is not available (a secure context is required).');
  const encoder = new TextEncoder();
  const target = challenge.challenge.toLowerCase();
  const started = performance.now();
  for (let start = 0; start <= challenge.maxnumber; start += batch) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    const end = Math.min(start + batch - 1, challenge.maxnumber);
    const numbers = Array.from({ length: end - start + 1 }, (_, i) => start + i);
    const digests = await Promise.all(numbers.map(n => subtle.digest('SHA-256', encoder.encode(challenge.salt + n))));
    const hit = digests.findIndex(d => toHex(d) === target);
    if (hit >= 0) {
      const payload = {
        algorithm: challenge.algorithm,
        challenge: challenge.challenge,
        number: numbers[hit],
        salt: challenge.salt,
        signature: challenge.signature,
        took: Math.round(performance.now() - started),
      };
      return btoa(JSON.stringify(payload));
    }
    await new Promise<void>(resolve => setTimeout(resolve, 0));
  }
  throw new Error('No ALTCHA solution within maxnumber.');
}

function toHex(buffer: ArrayBuffer): string {
  return Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
}
