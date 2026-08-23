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
 *
 * Jittered to between half a step and the full step, because every open page
 * loses the same socket at the same moment when Flux restarts: without it they
 * all retry in lockstep, forever.
 */
export function reconnectDelay(attempt: number): number {
  const step = attempt <= 0 ? FIRST_DELAY_MS : Math.min(FIRST_DELAY_MS * 2 ** attempt, MAX_DELAY_MS);

  return step * (0.5 + Math.random() * 0.5);
}
