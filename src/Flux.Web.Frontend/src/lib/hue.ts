import type { HueAreaData, HueColorLayout, HueData, HuePalette, HueState } from './protocol.ts';

/**
 * The largest latency offset the server accepts, either way.
 *
 * Mirrored from `HueOptions.MaxLatencyOffsetMs` so the slider cannot ask for a value
 * that comes straight back as `rejected`. Half a second is a whole beat at 120 BPM,
 * past which the offset is not a late light but a light on the wrong beat.
 */
export const LATENCY_OFFSET_LIMIT_MS = 500;

/**
 * The palettes the server accepts, in the order they are offered.
 *
 * `random` leads because it is the default and the least committal. Mirrored from
 * `LightPalette` rather than fetched: the list changes when the protocol does, and the
 * document is where that is agreed.
 */
export const PALETTES: readonly HuePalette[] = [
  'random',
  'warm',
  'cool',
  'neon',
  'sunset',
  'ocean',
  'forest',
  'party',
];

/**
 * The intervals worth offering, in bars.
 *
 * Zero is first because it is the extreme rather than the absence of a setting: it
 * means a new colour on every beat. The server accepts any whole number of bars up to
 * sixteen, so this ladder is a convenience and not the limit.
 */
export const COLOR_CHANGE_BARS: readonly number[] = [0, 1, 2, 4, 8, 16];

/**
 * How many colours the room can be in at once.
 *
 * Mirrored from `LightSettings.MaxColorCount`. Three, because every hue added takes
 * lights away from the others and past three a group stops reading as a part of the room
 * and starts reading as individual bulbs disagreeing.
 */
export const COLOR_COUNTS: readonly number[] = [1, 2, 3];

/**
 * How the room can be divided between those colours, in the order they are offered.
 *
 * `radial` leads because it is the default and the one with an argument behind it. The
 * other two are here to be tried against the room, which is the only way a look gets
 * settled. Mirrored from `LightColorLayout`.
 */
export const COLOR_LAYOUTS: readonly HueColorLayout[] = ['radial', 'alternating', 'random'];

/**
 * The slowest pulse the server accepts.
 *
 * Mirrored from `LightSettings.MaxPulseMs`. Two seconds is four beats at 120 BPM, past
 * which a pulse has not finished fading before the beat after next and stops being a
 * pulse at all.
 */
export const PULSE_LIMIT_MS = 2000;

/**
 * The top of both percentage controls.
 *
 * Mirrored from `LightSettings.MaxPercent`. One constant because brightness and depth
 * are both fractions of the same thing, and a slider that could ask for more than the
 * server accepts is a rejection nobody deserves.
 */
export const PERCENT_LIMIT = 100;

/**
 * The shortest and longest strobe cycle, as a percentage of a beat.
 *
 * Mirrored from `LightSettings.MinStrobeCycle` and `MaxStrobeCycle`, which carry the
 * reference scene's range across unchanged.
 */
export const STROBE_CYCLE_MIN = 10;
export const STROBE_CYCLE_MAX = 100;

/**
 * The shortest pulse a bulb can actually render, in milliseconds.
 *
 * The bridge forwards to a light at roughly 25 updates a second, so a bulb samples the
 * envelope about every 40 ms and a pulse needs two of those to show a rise and a fall.
 * Below this the result is not a dimmer flash, it is an *inconsistent* one: some beats
 * happen to be sampled near the peak and some near the tail, so the room flickers
 * unevenly at no setting anybody chose.
 *
 * Which is why the slider does not offer the range at all rather than warning about it.
 * The wire still accepts it - it is a legal instruction and narrowing a published range
 * is a different kind of change - but a control whose bottom end does something nobody
 * wants is a broken control, not an honest one.
 */
export const MIN_RENDERABLE_PULSE_MS = 80;

/**
 * The nearest pulse length that will actually render.
 *
 * Zero is kept reachable because it is a real setting rather than a short pulse: it
 * means no pulse at all, and a steady colour is a thing to want. Everything between
 * lands on whichever end is closer, so dragging through the dead band snaps out of it
 * instead of resting somewhere that misbehaves.
 */
export function renderablePulseMs(pulseMs: number): number {
  if (!Number.isFinite(pulseMs) || pulseMs <= 0) {
    return 0;
  }

  if (pulseMs >= MIN_RENDERABLE_PULSE_MS) {
    return Math.round(pulseMs);
  }

  return pulseMs < MIN_RENDERABLE_PULSE_MS / 2 ? 0 : MIN_RENDERABLE_PULSE_MS;
}

/**
 * What the bridge is doing, in a sentence.
 *
 * The server's own `message` wins wherever there is one, because it is the only text
 * that knows what actually went wrong - "the bridge is not answering on this network"
 * beats anything this page could infer from a state name.
 */
export function bridgeStatusLabel(hue: HueData | null): string {
  if (hue === null) {
    return 'asking Flux about the bridge';
  }

  if (hue.message !== null) {
    return hue.message;
  }

  switch (hue.state) {
    case 'disabled':
      return 'lights are switched off in the settings';
    case 'unpaired':
      return 'no bridge paired yet';
    case 'discovering':
      return 'looking for bridges on this network';
    case 'pairing':
      return 'press the link button on the bridge';
    case 'connecting':
      return 'asking the bridge what it has';
    case 'ready':
      return hue.selectedAreaId === null ? 'choose an entertainment room' : 'ready';
    default:
      return 'the bridge conversation failed';
  }
}

/**
 * Whether a "look for bridges" button should do anything.
 *
 * Off while the answer is already being fetched, and off when lights are switched off
 * entirely - the server would ignore the message in that case, and a button that
 * quietly does nothing is worse than one that is visibly unavailable.
 */
export function canDiscover(state: HueState | null): boolean {
  return state !== null && state !== 'disabled' && state !== 'discovering' && state !== 'pairing';
}

/**
 * Whether the pairing screen is the whole of the UI.
 *
 * `failed` is included: a bridge that stopped answering is one somebody may want to
 * re-find, and hiding discovery behind an error message is how a user ends up
 * reloading the page to get a button back.
 */
export function showsPairing(state: HueState | null): boolean {
  return state === 'unpaired' || state === 'discovering' || state === 'pairing' || state === 'failed';
}

/**
 * Whether the room controls belong on screen.
 *
 * Driven by there being rooms to show rather than by the state being exactly `ready`,
 * which is the fix for a panel that emptied itself. Any refresh passes briefly through
 * `connecting`, and a screen keyed on `ready` alone blanks every control on it for the
 * duration - so pressing a refresh button made the thing being refreshed disappear.
 */
export function showsRooms(hue: HueData | null): boolean {
  return hue !== null && hue.state !== 'disabled' && !showsPairing(hue.state) && hue.areas.length > 0;
}

/**
 * Whether the bridge needs somebody to do something before the lights can work.
 *
 * Narrower than "the room controls are not showing", and the difference decides
 * whether the settings drawer opens itself over the stage. A build with the lights
 * switched off, or one with no Hue support at all, shows no rooms and needs nothing
 * done about it - that is the beat lab running as intended, and a drawer that opened
 * itself every launch to say so would be a fault rather than a prompt.
 */
export function bridgeNeedsSetup(hue: HueData | null): boolean {
  if (hue === null || hue.state === 'disabled') {
    return false;
  }

  return showsPairing(hue.state) || hue.selectedAreaId === null;
}

/**
 * An area's size, and whether its one streaming session is already taken.
 *
 * The channel count is here because a user with two areas has usually named them
 * badly and can still tell three lights from ten. "In use" is the pre-flight showing
 * its work: it is the difference between a DTLS timeout somebody spends an evening on
 * and a sentence saying Hue Sync is still running.
 */
export function areaSummary(area: HueAreaData): string {
  const channels = `${area.channelCount} ${area.channelCount === 1 ? 'channel' : 'channels'}`;

  return area.streaming ? `${channels} - in use by something else` : channels;
}

/**
 * The pairing countdown, in words.
 *
 * Null outside a pairing window, and never a zero: a countdown of zero is a claim
 * that time is running out, and there is no window to run out of.
 */
export function countdownLabel(secondsRemaining: number | null): string | null {
  if (secondsRemaining === null || !Number.isFinite(secondsRemaining)) {
    return null;
  }

  const seconds = Math.max(0, Math.round(secondsRemaining));

  return `${seconds} ${seconds === 1 ? 'second' : 'seconds'} left`;
}

/**
 * A latency offset in words, signed, because the sign is the whole meaning.
 *
 * Zero is "on the beat" rather than "0 ms early", which would read as a measurement
 * of something rather than as the absence of a correction.
 */
export function offsetLabel(offsetMs: number): string {
  if (!Number.isFinite(offsetMs) || Math.round(offsetMs) === 0) {
    return 'on the beat';
  }

  const rounded = Math.round(offsetMs);

  return rounded > 0 ? `${rounded} ms early` : `${Math.abs(rounded)} ms late`;
}

/**
 * The share of a beat an offset amounts to, or null when there is no tempo to measure
 * it against.
 *
 * This exists because of a real evening lost to it. An offset of 400 ms is a sensible
 * looking number and at 130 BPM it is 0.87 of a beat, which moves every colour change
 * onto the beat before the one it was aimed at - so the lights looked wrong in a way
 * that pointed at the bar counting rather than at the slider. The document already
 * warned that half a second is a whole beat; the page said nothing at all.
 */
export function offsetInBeats(offsetMs: number, bpm: number | null): number | null {
  if (bpm === null || !Number.isFinite(bpm) || bpm <= 0 || !Number.isFinite(offsetMs)) {
    return null;
  }

  return Math.abs(offsetMs) / (60000 / bpm);
}

/**
 * Whether an offset is far enough into a beat to be worth warning about.
 *
 * A third of a beat, which is not a cliff so much as the point where "the light is
 * late" stops being the explanation somebody should reach for first.
 */
export function offsetIsNearABeat(offsetMs: number, bpm: number | null): boolean {
  const beats = offsetInBeats(offsetMs, bpm);

  return beats !== null && beats >= 0.33;
}

/**
 * The offset measured in beats, in words, for the line under the slider.
 *
 * Null when there is no tempo, because inventing one is the thing a beat display must
 * never do - and an offset with nothing to compare it against is exactly as meaningful
 * as the millisecond count already shown.
 */
export function offsetBeatsLabel(offsetMs: number, bpm: number | null): string | null {
  const beats = offsetInBeats(offsetMs, bpm);

  if (beats === null || Math.round(offsetMs) === 0) {
    return null;
  }

  const share = `${beats.toFixed(2)} of a beat at ${Math.round(bpm as number)} BPM`;

  return beats >= 0.33 ? `${share} - large enough to land on the wrong beat` : share;
}

/**
 * How bright the room is, in words.
 *
 * Zero says "off" rather than "0%", because a light at zero is indistinguishable from a
 * stream that never opened and this is the one place that can say which it is.
 */
export function brightnessLabel(brightness: number): string {
  if (!Number.isFinite(brightness) || Math.round(brightness) <= 0) {
    return 'off';
  }

  return `${Math.round(brightness)}%`;
}

/**
 * How much a beat stands out, in words.
 *
 * Both ends are named rather than shown as a percentage, because both are a different
 * behaviour rather than more or less of the same one: no dip at all, and a fade to
 * nothing.
 */
export function pulseDepthLabel(depth: number): string {
  if (!Number.isFinite(depth) || Math.round(depth) <= 0) {
    return 'no dip';
  }

  const rounded = Math.round(depth);

  return rounded >= 100 ? 'down to black' : `${rounded}%`;
}

/**
 * How often the colour changes, in words.
 *
 * Zero says "every beat" and not "every 0 bars", which is the whole reason this
 * function exists: the setting reads backwards, because switching the interval off does
 * not stop the colours changing, it removes the interval.
 */
export function colorChangeLabel(bars: number): string {
  if (!Number.isFinite(bars) || Math.round(bars) <= 0) {
    return 'every beat';
  }

  const rounded = Math.round(bars);

  return rounded === 1 ? 'every bar' : `every ${rounded} bars`;
}

/**
 * How many colours the room is in, in words.
 *
 * "one colour" rather than "1", because the row beside it reads "every 4 bars" and a
 * bare numeral in a row of phrases reads as a quantity of something unnamed.
 */
export function colorCountLabel(count: number): string {
  const rounded = Math.max(1, Math.round(count));

  return rounded === 1 ? 'one colour' : `${rounded} colours`;
}

/**
 * How often the strobe flashes, in words.
 *
 * Flashes per beat rather than the percentage the wire carries, because the percentage
 * reads backwards - a smaller number is faster - and "twice a beat" is what somebody
 * standing in the room would say.
 */
export function strobeCycleLabel(cycle: number): string {
  const rounded = Math.round(cycle);

  if (!Number.isFinite(rounded) || rounded <= 0) {
    return 'off';
  }

  const perBeat = 100 / rounded;

  if (perBeat === 1) {
    return 'once a beat';
  }

  if (perBeat === 2) {
    return 'twice a beat';
  }

  return `${Number.isInteger(perBeat) ? perBeat : perBeat.toFixed(1)} a beat`;
}

/**
 * How long a beat's pulse lasts, in words.
 *
 * Zero says "no pulse" rather than "0 ms", for the same reason: it is a steady colour
 * and not an instantaneous flash. A one-frame pulse at 50 Hz would be a 20 ms strobe
 * nobody asked for.
 */
export function pulseLabel(pulseMs: number): string {
  if (!Number.isFinite(pulseMs) || Math.round(pulseMs) <= 0) {
    return 'no pulse';
  }

  return `${Math.round(pulseMs)} ms`;
}
