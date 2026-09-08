import type { AlignmentMethod } from './protocol.ts';

/** The placeholder for a value Flux does not have. Never a zero. */
export const UNKNOWN = '--';

/**
 * What put the bar where it is, in words.
 *
 * How it was anchored is worth as much as the fact that it was. A bar anchored
 * by a cue press is a statement about the deck and one anchored by a tap is a
 * statement about the person holding it, so a cue that silently never arrives
 * is invisible unless the two are told apart on screen.
 *
 * An anchored bar with no method is not a state the server produces, and it is
 * still rendered as the plain fact rather than as an error: the position is
 * genuinely anchored either way, which is what the reading is for.
 */
export function alignmentLabel(aligned: boolean, method: AlignmentMethod | null): string {
  if (!aligned) {
    return 'counting beats - bar not anchored yet';
  }

  switch (method) {
    case 'cue':
      return 'bar anchored by a cue press';
    case 'tap':
      return 'bar anchored by a tap';
    case 'drop':
      return 'bar anchored by a drop';
    default:
      return 'bar anchored';
  }
}

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

/** A whole-number counter, or a blank where the source keeps no such count. */
export function formatCount(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return UNKNOWN;
  }

  return Math.trunc(value).toString();
}

/** A measured rate, to one decimal, because the question is only ever how far under. */
export function formatRate(perSecond: number | null | undefined): string {
  if (perSecond === null || perSecond === undefined || !Number.isFinite(perSecond)) {
    return UNKNOWN;
  }

  return perSecond.toFixed(1);
}

/** Above this an elapsed time is shown in seconds instead of milliseconds. */
const AGO_IN_SECONDS_ABOVE_MS = 10_000;

/**
 * How long ago something last happened.
 *
 * Milliseconds while they carry meaning and seconds once they stop: beats are
 * half a second apart, so the digits are the whole measurement early on, and a
 * tap nobody has repeated for a minute would otherwise render as five digits of
 * noise in a cell sized for three.
 */
export function formatAgo(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined || !Number.isFinite(milliseconds)) {
    return UNKNOWN;
  }

  const elapsed = Math.max(0, milliseconds);

  if (elapsed < AGO_IN_SECONDS_ABOVE_MS) {
    return `${Math.round(elapsed)} ms`;
  }

  return `${(elapsed / 1000).toFixed(1)} s`;
}
