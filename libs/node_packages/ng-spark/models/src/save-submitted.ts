/**
 * The answer to a create or an update the server diverted instead of saving (commerce X7): an
 * `ISaveReplacement` recorded it — a document proposal waiting for approval — and answered `202 Accepted`.
 * Nothing was written yet. Not an error: the save was accepted, it just takes effect once approved.
 */
export interface SparkSaveSubmitted {
  submitted: {
    /** What the save became, for the client to word it: `approval` for a document proposal. */
    kind: string;
    /** The id of what was recorded (the proposal). */
    referenceId: string;
  };
}

/** Whether a create or update result is a {@link SparkSaveSubmitted} rather than the saved object. */
export function isSaveSubmitted(result: unknown): result is SparkSaveSubmitted {
  const submitted = (result as Partial<SparkSaveSubmitted> | null | undefined)?.submitted;
  return !!submitted && typeof submitted.kind === 'string' && typeof submitted.referenceId === 'string';
}
