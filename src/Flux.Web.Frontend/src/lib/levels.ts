/**
 * Turning a level into something a human can read off a meter.
 *
 * The wire carries levels on a linear 0 to 1 scale, and drawing that directly
 * would make the meter useless: a healthy master bus sits around -20 dBFS, which
 * is 0.1 linear, so every real signal would be a tenth of a bar and the whole
 * top nine tenths would be reserved for clipping. Ears are logarithmic and so are
 * the measurements this project has been taking since Phase 0, which are all
 * written in dBFS. The meter matches them.
 */

import { UNKNOWN } from './format.ts';

/**
 * Where the meter bottoms out, in dBFS.
 *
 * Deep enough that a quiet passage still moves the bar, shallow enough that the
 * useful range is not squeezed into the last centimetre. Anything below this is
 * being read as nothing, which for this instrument's purpose it is.
 */
export const METER_FLOOR_DBFS = -60;

/**
 * A linear level as dBFS, or negative infinity for digital silence.
 *
 * Silence is deliberately not clamped to the floor here. A bit-exact zero is a
 * distinct measurement - a stopped deck, or a closed fader, which is the fault
 * that cost this project two recordings - and the caller decides how to say so.
 */
export function toDbfs(level: number): number {
  if (!Number.isFinite(level) || level <= 0) {
    return Number.NEGATIVE_INFINITY;
  }

  return 20 * Math.log10(Math.min(1, level));
}

/** Where a level sits on the bar, from 0 at the floor to 1 at full scale. */
export function meterFraction(level: number): number {
  const db = toDbfs(level);

  if (db === Number.NEGATIVE_INFINITY) {
    return 0;
  }

  return Math.min(1, Math.max(0, (db - METER_FLOOR_DBFS) / -METER_FLOOR_DBFS));
}

/**
 * How fast the bar falls, in bar widths per second.
 *
 * The bar spans the decibels between the floor and full scale, so a fifth of it
 * a second is about 12 dB a second, which is the region broadcast peak meters
 * have used for decades. The fall is linear in bar width rather than a multiplier on
 * the level, and that is the point: the bar is already logarithmic, so a linear
 * slide down it is a constant number of decibels a second, which is what makes a
 * meter readable.
 */
export const METER_RELEASE_PER_SECOND = 0.2;

/**
 * Where the bar sits after one redraw, given where it is and where it should be.
 *
 * Instant on the way up and slow on the way down, which is a peak meter's whole
 * behaviour. The rise must not be smoothed: the loudest moment in the window is
 * the reading, and averaging it away would hide exactly the clipping the peak
 * tick exists to show. The fall is a display convention and is honest about it -
 * nothing has been measured between the four windows a second the meter gets.
 */
export function releaseTowards(shown: number, target: number, elapsedMs: number): number {
  if (!Number.isFinite(shown)) {
    return target;
  }

  if (target >= shown) {
    return target;
  }

  if (!Number.isFinite(elapsedMs) || elapsedMs <= 0) {
    return shown;
  }

  return Math.max(target, shown - (METER_RELEASE_PER_SECOND * elapsedMs) / 1000);
}

/**
 * How quickly the bar rises, as an exponential time constant in milliseconds.
 *
 * A peak indicator rises instantly and should; an average does not. RMS is
 * already an average over a window, and snapping the bar to each new window
 * makes it twitch four times a second at exactly the rate the windows close.
 * Moving-coil meters integrate the rise for the same reason, and take roughly
 * this long to do it.
 */
export const METER_ATTACK_MS = 120;

/**
 * Where the bar sits on the way up: most of the way there, quickly.
 *
 * Exponential rather than linear so a large jump still arrives promptly while a
 * small one is barely eased at all, which is what keeps the meter feeling
 * attached to the music instead of lagging behind it.
 */
export function attackTowards(shown: number, target: number, elapsedMs: number): number {
  if (!Number.isFinite(shown)) {
    return target;
  }

  if (!Number.isFinite(elapsedMs) || elapsedMs <= 0) {
    return shown;
  }

  return shown + (target - shown) * (1 - Math.exp(-elapsedMs / METER_ATTACK_MS));
}

/**
 * One redraw of an averaged level: eased up, slid down.
 *
 * The peak tick deliberately does not use this. That one is the loudest single
 * sample in the window, and easing it would understate a clip, which is the
 * fault it exists to report.
 */
export function advanceMeter(shown: number, target: number, elapsedMs: number): number {
  return target > shown
    ? attackTowards(shown, target, elapsedMs)
    : releaseTowards(shown, target, elapsedMs);
}

/**
 * A level as text.
 *
 * Three outcomes and they are not the same thing: a number that was measured, a
 * silence that was measured, and a device nobody is listening to. The last one
 * must never render as a number, which is the rule the protocol made optional
 * fields for in the first place.
 */
export function formatLevel(level: number | null | undefined): string {
  if (level === null || level === undefined || !Number.isFinite(level)) {
    return UNKNOWN;
  }

  const db = toDbfs(level);

  return db === Number.NEGATIVE_INFINITY ? 'silent' : `${db.toFixed(1)} dB`;
}
