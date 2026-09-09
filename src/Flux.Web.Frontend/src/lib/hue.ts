import type { HueAreaData, HueData, HueState } from './protocol.ts';

/**
 * The largest latency offset the server accepts, either way.
 *
 * Mirrored from `HueOptions.MaxLatencyOffsetMs` so the slider cannot ask for a value
 * that comes straight back as `rejected`. Half a second is a whole beat at 120 BPM,
 * past which the offset is not a late light but a light on the wrong beat.
 */
export const LATENCY_OFFSET_LIMIT_MS = 500;

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
