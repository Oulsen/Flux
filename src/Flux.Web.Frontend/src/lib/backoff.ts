/** First wait after a connection drops. */
const FIRST_DELAY_MS = 500;

/** Longest wait between attempts. */
const MAX_DELAY_MS = 10_000;

/**
 * How long to wait before reconnection attempt number `attempt` (0-based).
 *
 * Capped rather than escalating, for the same reason the Link listener's backoff
 * is: the usual cause is the app restarting during development or a phone waking
 * up, and neither deserves a wait that keeps growing. Ten seconds is the longest
 * a page should ever look dead after Flux comes back.
 */
export function reconnectDelay(attempt: number): number {
  if (attempt <= 0) {
    return FIRST_DELAY_MS;
  }

  return Math.min(FIRST_DELAY_MS * 2 ** attempt, MAX_DELAY_MS);
}
