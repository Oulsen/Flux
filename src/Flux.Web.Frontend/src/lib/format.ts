/** The placeholder for a value Flux does not have. Never a zero. */
export const UNKNOWN = '--';

/**
 * Tempo, always to two decimals.
 *
 * Fixed width on purpose: a readout that gains and loses a digit shifts
 * everything beside it four times a second, which reads as a glitch rather than
 * as a measurement.
 */
export function formatBpm(bpm: number | null | undefined): string {
  if (bpm === null || bpm === undefined || !Number.isFinite(bpm)) {
    return UNKNOWN;
  }

  return bpm.toFixed(2);
}

/**
 * The re-anchor drift metric, to four decimals.
 *
 * Four because the number it is watched against is 0.0000: fewer digits would
 * round the first sign of trouble away.
 */
export function formatDrift(beats: number | null | undefined): string {
  if (beats === null || beats === undefined || !Number.isFinite(beats)) {
    return UNKNOWN;
  }

  return beats.toFixed(4);
}

/** A round trip in whole milliseconds. */
export function formatLatency(milliseconds: number | null): string {
  if (milliseconds === null || !Number.isFinite(milliseconds)) {
    return UNKNOWN;
  }

  return `${Math.max(0, Math.round(milliseconds))} ms`;
}
