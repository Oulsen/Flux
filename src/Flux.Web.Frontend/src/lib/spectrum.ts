/**
 * The arithmetic behind the visualizer, with no canvas anywhere near it.
 *
 * Drawing a spectrum is two separate jobs and only one of them is drawing. The
 * other is deciding what a bar's height should be at this instant, given that
 * frames arrive at 30 Hz and the screen redraws at roughly twice that: without an
 * envelope the bars step visibly, and with the wrong envelope they either smear
 * the kick away or flicker. That decision is here, as pure functions, because it
 * is the half that can be wrong in a way no screenshot would settle.
 *
 * The constants are the scene's, transcribed as coded from
 * `.plan/reference/oulsen-dj-scene.html`, and the rest of Flux's audio numbers
 * are documented the same way in `docs/audio-constants.md`.
 */

import type { SpectrumBeat, SpectrumData } from './protocol.ts';

/**
 * The frame rate the scene's decay constants assume.
 *
 * The scene multiplies by a fixed factor once per rendered frame, which ties how
 * fast a bar falls to how fast the monitor happens to refresh. On the 60 Hz
 * display it was written for that is invisible; on the 144 Hz one somebody plugs
 * in at the booth the whole visualizer would decay two and a half times too fast.
 * Every factor below is therefore the scene's per-frame number *at this rate*,
 * and is raised to the elapsed time to get the factor actually applied.
 */
export const REFERENCE_FPS = 60;

/** `beatFlashIntensity *= 0.85` per frame. */
export const FLASH_DECAY_PER_FRAME = 0.85;

/** `audioLevel *= 0.95` per frame, and only ever on the way down. */
export const LEVEL_DECAY_PER_FRAME = 0.95;

/** `beatFlashIntensity = 0.5`, the scene's soft beat flash. */
export const FLASH_ON_BEAT = 0.5;

/** `beatFlashIntensity = 1.0`, the scene's full flash for a strong beat. */
export const FLASH_ON_STRONG_BEAT = 1;

/** Spectrum bins arrive quantized to a byte, as the protocol specifies. */
const BIN_MAX = 255;

/**
 * What the visualizer is holding between frames.
 *
 * `targets` is kept separately from `levels` rather than folded into it, and that
 * is the whole reason this looks like more state than it needs. The envelope has
 * to be evaluated against the newest measurement on every rendered frame, not
 * only on the frames a measurement arrives with. Decaying between frames and
 * jumping back on arrival would give every sustained sound a 30 Hz ripple, which
 * is a shimmer nothing in the music is doing.
 */
export interface SpectrumVisual {
  /** The newest frame's levels, 0 to 1, held until the next frame replaces them. */
  targets: number[];
  /** What to draw, 0 to 1: instant on the way up, slow on the way down. */
  levels: number[];
  /** The beat flash, 1 at a strong beat and decaying toward nothing. */
  flash: number;
}

/** A visualizer that has been told nothing yet. */
export function emptyVisual(binCount: number): SpectrumVisual {
  const size = Math.max(0, Math.trunc(binCount));

  return {
    targets: new Array<number>(size).fill(0),
    levels: new Array<number>(size).fill(0),
    flash: 0,
  };
}

/** One byte of the wire's spectrum as a level from 0 to 1. */
export function normalizeBin(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  return Math.min(1, Math.max(0, value / BIN_MAX));
}

/** How bright the flash goes for what the detector made of a frame. */
export function flashFor(beat: SpectrumBeat): number {
  switch (beat) {
    case 'strong':
      return FLASH_ON_STRONG_BEAT;
    case 'beat':
      return FLASH_ON_BEAT;
    default:
      return 0;
  }
}

/**
 * A per-frame decay factor scaled to how long the frame actually took.
 *
 * Time that did not pass decays nothing, which matters because two redraws can
 * share a timestamp. Time that ran away decays toward zero on its own, which is
 * the right answer for a tab that was in the background: what comes back should
 * be the music now, not the bar heights from before the laptop lid closed.
 */
export function decayFactor(perFrame: number, elapsedMs: number): number {
  if (!Number.isFinite(elapsedMs) || elapsedMs <= 0) {
    return 1;
  }

  return Math.pow(perFrame, (elapsedMs * REFERENCE_FPS) / 1000);
}

/**
 * Takes a new frame: the levels are the new measurement, the flash can only rise.
 *
 * The flash is raised rather than assigned so a soft beat cannot cut short the
 * tail of the strong one before it, which is the scene's own `Math.max` on the
 * grid-beat path. It is set here and nowhere else, because a frame held across
 * several redraws would otherwise re-trigger its own flash on each of them and
 * the thing would never decay at all.
 */
export function receiveFrame(visual: SpectrumVisual, frame: SpectrumData): SpectrumVisual {
  const targets = frame.bins.map(normalizeBin);

  return {
    targets,
    // A frame of a different width is a server drawing a different spectrum, so
    // there is nothing meaningful to smooth from. Start from the measurement.
    levels: targets.length === visual.levels.length ? visual.levels : [...targets],
    flash: Math.max(visual.flash, flashFor(frame.beat)),
  };
}

/**
 * Moves the drawn state on by one rendered frame.
 *
 * The envelope is the scene's: instant attack, slow release. A kick has to reach
 * its full height on the frame it lands, because a rise smoothed over even two
 * frames is a beat marker arriving late, and late is the one thing this display
 * exists to rule out. The fall is slow because that is what makes a bar readable
 * at all.
 */
export function advanceVisual(visual: SpectrumVisual, elapsedMs: number): SpectrumVisual {
  const levelDecay = decayFactor(LEVEL_DECAY_PER_FRAME, elapsedMs);

  return {
    targets: visual.targets,
    levels: visual.levels.map((level, index) => {
      const target = visual.targets[index] ?? 0;

      // The comparison includes equality, where the scene's does not. A level
      // sitting exactly on its measurement would otherwise decay and snap back
      // on alternate frames, and a held tone would shimmer at the frame rate
      // while doing nothing at all in the room.
      return target >= level ? target : level * levelDecay;
    }),
    flash: visual.flash * decayFactor(FLASH_DECAY_PER_FRAME, elapsedMs),
  };
}
