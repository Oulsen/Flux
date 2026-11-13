/*
 * The deck strip's arithmetic: where the playhead is between state frames, which beat
 * of the track's grid a moment falls on, and where Flux's own bar 1 lands on that grid.
 *
 * Kept out of the component for the reason `scene.ts` is: these are the parts that can
 * be wrong without looking wrong. A strip whose playhead runs a few percent fast still
 * scrolls smoothly, and a bar mark one beat off is drawn as confidently as a right one,
 * which is exactly the mistake the strip exists to make visible.
 */

import type { DeckData, TrackData } from './protocol.ts';

/** A track as the strip draws it: waveforms decoded, grid as two arrays of one length. */
export interface DeckTrack {
  /** Waveform columns per second of track at normal speed, for both waveforms; 0 with neither. */
  entriesPerSecond: number;
  /** The 3-band waveform: three heights per column, low, mid, high, each 0 to 127; empty without one. */
  lowMidHigh: Uint8Array;
  /** The RGB waveform: four bytes per column, height 0 to 31 then red, green, blue each 0 to 7; empty without one. */
  heightRgb: Uint8Array;
  beatTimesMs: number[];
  beatsInBar: number[];
}

/** The tallest a band of the 3-band waveform is on disk. */
export const MAX_HEIGHT = 127;

/** The tallest a column of the RGB waveform is, and the most of each colour. */
export const RGB_MAX_HEIGHT = 31;
export const RGB_MAX_COLOR = 7;

/** Decodes the body of `/api/track/{track}`. */
export function decodeTrack(data: TrackData): DeckTrack {
  return {
    entriesPerSecond: data.entriesPerSecond,
    lowMidHigh: fromBase64(data.threeBand),
    heightRgb: fromBase64(data.rgb),
    beatTimesMs: data.beatTimesMs,
    beatsInBar: data.beatsInBar,
  };
}

function fromBase64(text: string | null | undefined): Uint8Array {
  // Undefined as well as null: an answer from a build before the field existed has none.
  if (text === null || text === undefined) {
    return new Uint8Array(0);
  }

  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }

  return bytes;
}

/**
 * The RGB column a pixel shows over columns `from` up to `to`: the tallest among them,
 * in that column's own colour, so a peak keeps the colour of the sound that made it.
 * Null outside the waveform.
 */
export function rgbOver(heightRgb: Uint8Array, from: number, to: number): [number, number, number, number] | null {
  const count = heightRgb.length / 4;
  const start = Math.max(0, Math.floor(from));
  const end = Math.min(count, Math.max(start + 1, Math.ceil(to)));
  let best = -1;
  for (let i = start; i < end; i++) {
    if (best < 0 || heightRgb[i * 4] > heightRgb[best * 4]) {
      best = i;
    }
  }

  return best < 0
    ? null
    : [heightRgb[best * 4], heightRgb[best * 4 + 1], heightRgb[best * 4 + 2], heightRgb[best * 4 + 3]];
}

/** A deck frame and when, on this page's clock, it arrived. */
export interface HeldDeck {
  deck: DeckData;
  receivedAt: number;
}

/**
 * Where the playhead is at `now`, in track milliseconds: the frame's position carried
 * on at the deck's own rate while it plays, held where it is while it does not.
 */
export function playheadAt(held: HeldDeck, now: number): number {
  const { deck, receivedAt } = held;
  return deck.playing ? deck.positionMs + Math.max(0, now - receivedAt) * deck.rate : deck.positionMs;
}

/**
 * How far a shown playhead may be from the frame's before it jumps rather than glides.
 * Flux's own number, reasoned: a frame arrives every 50 ms with up to a few tens of
 * milliseconds of jitter, so anything under a quarter of a second is that jitter and is
 * eased away, and anything over it is a seek, a loop or a new track, which should be
 * where it is at once.
 */
export const SNAP_MS = 250;

/**
 * Of the gap between what is shown and where the playhead is, the share closed each
 * animation frame. Flux's own number: about a tenth of a second to close a jitter at
 * 60 frames a second, slow enough not to wobble and quick enough not to lag a beat.
 */
export const EASE_PER_FRAME = 0.15;

/** The next shown position: `target` itself after a jump, otherwise eased towards it. */
export function easePlayhead(shown: number | null, target: number): number {
  if (shown === null || Math.abs(target - shown) > SNAP_MS) {
    return target;
  }

  return shown + (target - shown) * EASE_PER_FRAME;
}

/**
 * The fractional beat of the grid at `ms`: 2.5 is halfway from the third beat to the
 * fourth, counting from 0. Before the first beat and past the last it counts on at the
 * nearest interval, since a track's beats do not stop where its grid does.
 */
export function beatAt(beatTimesMs: number[], ms: number): number {
  const count = beatTimesMs.length;
  if (count === 0) {
    return 0;
  }

  if (count === 1) {
    return 0;
  }

  if (ms < beatTimesMs[0]) {
    return (ms - beatTimesMs[0]) / interval(beatTimesMs, 0);
  }

  // The last beat at or before ms.
  let lo = 0;
  let hi = count - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (beatTimesMs[mid] <= ms) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  return lo + (ms - beatTimesMs[lo]) / interval(beatTimesMs, lo);
}

/** The time to the next beat from beat `at`, or to it from the one before at the end; never 0. */
function interval(beatTimesMs: number[], at: number): number {
  const next = at + 1 < beatTimesMs.length ? beatTimesMs[at + 1] - beatTimesMs[at] : beatTimesMs[at] - beatTimesMs[at - 1];
  return next > 0 ? next : 1;
}

/**
 * Which of the grid's beats Flux counts as bar 1, given where the playhead was in the
 * grid (`gridBeat`) and where Flux's grid was (`fluxBeat`, `positionInBar`) on the same
 * state frame. Answers the grid beat index of one of Flux's downbeats; every bar's
 * worth of beats from it is another.
 *
 * The two are paired by their fractional parts rather than by flooring each: the deck
 * and Flux's grid agree on the phase to a few milliseconds, but a few milliseconds
 * either side of a beat floors them to neighbouring beats, and the mark would flicker a
 * beat back and forth on every beat.
 */
export function fluxDownbeat(gridBeat: number, fluxBeat: number, positionInBar: number): number {
  const fluxFraction = fluxBeat - Math.floor(fluxBeat);
  const onBeat = Math.round(gridBeat - fluxFraction);
  return onBeat - (positionInBar - 1);
}

/** Whether grid beat `beat` is one of Flux's downbeats, given one of them. */
export function isFluxDownbeat(beat: number, downbeat: number, quantum: number): boolean {
  return (((beat - downbeat) % quantum) + quantum) % quantum === 0;
}

/**
 * The tallest of each band over waveform columns `from` up to `to`, so a pixel that
 * covers several columns shows the peak among them rather than whichever one it landed
 * on, which would flicker as the strip scrolls. Zeros outside the waveform.
 */
export function peakOver(lowMidHigh: Uint8Array, from: number, to: number): [number, number, number] {
  const count = lowMidHigh.length / 3;
  const start = Math.max(0, Math.floor(from));
  const end = Math.min(count, Math.max(start + 1, Math.ceil(to)));
  let low = 0;
  let mid = 0;
  let high = 0;
  for (let i = start; i < end; i++) {
    low = Math.max(low, lowMidHigh[i * 3]);
    mid = Math.max(mid, lowMidHigh[i * 3 + 1]);
    high = Math.max(high, lowMidHigh[i * 3 + 2]);
  }

  return [low, mid, high];
}
