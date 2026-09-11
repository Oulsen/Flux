import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  areaSummary,
  brightnessLabel,
  bridgeNeedsSetup,
  bridgeStatusLabel,
  canDiscover,
  COLOR_CHANGE_BARS,
  colorChangeLabel,
  countdownLabel,
  LATENCY_OFFSET_LIMIT_MS,
  MIN_RENDERABLE_PULSE_MS,
  offsetBeatsLabel,
  offsetInBeats,
  offsetIsNearABeat,
  offsetLabel,
  PALETTES,
  PERCENT_LIMIT,
  pulseDepthLabel,
  PULSE_LIMIT_MS,
  pulseLabel,
  renderablePulseMs,
  showsPairing,
  showsRooms,
} from './hue.ts';
import type { HueAreaData, HueData } from './protocol.ts';

function connection(over: Partial<HueData> = {}): HueData {
  return {
    state: 'unpaired',
    bridge: null,
    discovered: [],
    areas: [],
    selectedAreaId: null,
    pairingSecondsRemaining: null,
    latencyOffsetMs: 0,
    engine: { palette: 'random', colorChangeBars: 4, pulseMs: 240, brightness: 100, pulseDepth: 85, colorCount: 1, colorLayout: 'radial' },
    message: null,
    ...over,
  };
}

function area(over: Partial<HueAreaData> = {}): HueAreaData {
  return {
    id: '44444444-4444-4444-4444-444444444444',
    name: 'Booth',
    channelCount: 3,
    streaming: false,
    ...over,
  };
}

test('no frame yet is not the same as no bridge', () => {
  // The server sends a hue frame the moment a socket opens, so null lasts about as
  // long as the handshake. Rendering "unpaired" through it would flash a pairing
  // screen at somebody whose lights are already running.
  assert.notEqual(bridgeStatusLabel(null), bridgeStatusLabel(connection({ state: 'unpaired' })));
});

test("the server's own message wins over anything this page could infer", () => {
  const failed = connection({ state: 'failed', message: 'The paired bridge is not answering.' });

  assert.equal(bridgeStatusLabel(failed), 'The paired bridge is not answering.');
});

test('an unanswered pairing window reads as a fact rather than a failure', () => {
  const unpaired = connection({ state: 'unpaired', message: "Didn't see the button press." });

  assert.equal(bridgeStatusLabel(unpaired), "Didn't see the button press.");
});

test('ready without an area chosen says so instead of claiming to be ready', () => {
  assert.equal(bridgeStatusLabel(connection({ state: 'ready' })), 'choose an entertainment room');
  assert.equal(
    bridgeStatusLabel(connection({ state: 'ready', selectedAreaId: '44444444-4444-4444-4444-444444444444' })),
    'ready',
  );
});

test('discovery is unavailable while it is already happening or cannot happen', () => {
  assert.equal(canDiscover('unpaired'), true);
  assert.equal(canDiscover('ready'), true);
  assert.equal(canDiscover('failed'), true);

  assert.equal(canDiscover('discovering'), false);
  assert.equal(canDiscover('pairing'), false);
  assert.equal(canDiscover('disabled'), false);
  assert.equal(canDiscover(null), false);
});

test('a failed bridge still shows the pairing screen', () => {
  // Hiding discovery behind an error message is how somebody ends up reloading the
  // page to get a button back.
  assert.equal(showsPairing('failed'), true);
  assert.equal(showsPairing('unpaired'), true);
  assert.equal(showsPairing('ready'), false);
  assert.equal(showsPairing('disabled'), false);
});

test('the room controls survive a refresh passing through connecting', () => {
  // The panel used to be keyed on state === 'ready', so pressing refresh made the
  // thing being refreshed vanish for as long as it took.
  const withRooms = [area()];

  assert.equal(showsRooms(connection({ state: 'ready', areas: withRooms })), true);
  assert.equal(showsRooms(connection({ state: 'connecting', areas: withRooms })), true);

  assert.equal(showsRooms(connection({ state: 'ready', areas: [] })), false);
  assert.equal(showsRooms(connection({ state: 'unpaired', areas: withRooms })), false);
  assert.equal(showsRooms(connection({ state: 'disabled', areas: withRooms })), false);
  assert.equal(showsRooms(null), false);
});

test('a build with the lights switched off needs nothing set up', () => {
  // This is what decides whether the settings drawer opens itself over the stage, so
  // the two ways of having no rooms have to be told apart: one is somebody who cannot
  // use Flux until they act, the other is the beat lab running exactly as configured.
  assert.equal(bridgeNeedsSetup(connection({ state: 'disabled' })), false);
  assert.equal(bridgeNeedsSetup(null), false);

  assert.equal(bridgeNeedsSetup(connection({ state: 'unpaired' })), true);
  assert.equal(bridgeNeedsSetup(connection({ state: 'failed' })), true);
  assert.equal(
    bridgeNeedsSetup(connection({ state: 'ready', areas: [area()], selectedAreaId: null })),
    true,
  );

  assert.equal(
    bridgeNeedsSetup(connection({ state: 'ready', areas: [area()], selectedAreaId: area().id })),
    false,
  );
});

test('an area says how big it is, and singular counts read as English', () => {
  assert.equal(areaSummary(area({ channelCount: 3 })), '3 channels');
  assert.equal(areaSummary(area({ channelCount: 1 })), '1 channel');
});

test('an area somebody else is streaming to says so before anyone presses start', () => {
  assert.equal(areaSummary(area({ streaming: true })), '3 channels - in use by something else');
});

test('there is no countdown outside a pairing window, and never a zero standing in for one', () => {
  assert.equal(countdownLabel(null), null);
  assert.equal(countdownLabel(Number.NaN), null);
  assert.equal(countdownLabel(42), '42 seconds left');
  assert.equal(countdownLabel(1), '1 second left');
});

test('an offset reads as early or late, and zero reads as neither', () => {
  assert.equal(offsetLabel(0), 'on the beat');
  assert.equal(offsetLabel(90), '90 ms early');
  assert.equal(offsetLabel(-120), '120 ms late');
});

test('the slider cannot ask for a value the server will refuse', () => {
  // Mirrored from HueOptions.MaxLatencyOffsetMs. If the two ever disagree, the
  // symptom is a slider that rejects at one end and nowhere else.
  assert.equal(LATENCY_OFFSET_LIMIT_MS, 500);
});

test('an interval of no bars reads as every beat rather than as every zero bars', () => {
  // The setting reads backwards on purpose: switching the interval off does not stop
  // the colours changing, it removes the interval.
  assert.equal(colorChangeLabel(0), 'every beat');
  assert.equal(colorChangeLabel(1), 'every bar');
  assert.equal(colorChangeLabel(4), 'every 4 bars');
  assert.equal(colorChangeLabel(Number.NaN), 'every beat');
});

test('no pulse reads as no pulse rather than as a pulse of no length', () => {
  assert.equal(pulseLabel(0), 'no pulse');
  assert.equal(pulseLabel(240), '240 ms');
  assert.equal(pulseLabel(Number.NaN), 'no pulse');
});

test('the controls offer exactly the palettes the server accepts', () => {
  // Mirrored from LightPalette. If the two disagree the symptom is a chip that comes
  // straight back as rejected, on one palette, for whoever picked it.
  assert.deepEqual(PALETTES, ['random', 'warm', 'cool', 'neon', 'sunset', 'ocean', 'forest', 'party']);
});

test('every interval and pulse the controls offer is inside the documented range', () => {
  assert.equal(PULSE_LIMIT_MS, 2000);

  for (const bars of COLOR_CHANGE_BARS) {
    assert.ok(Number.isInteger(bars) && bars >= 0 && bars <= 16, `${bars} bars is outside 0 to 16`);
  }
});

test('an offset is measured in beats, which is the reading milliseconds cannot give', () => {
  // 130 BPM is a 461.5 ms beat, so 400 ms is most of one - which is how a sensible
  // looking number moved every colour change onto the beat before the intended one.
  assert.equal(offsetInBeats(400, 130)?.toFixed(2), '0.87');
  assert.equal(offsetInBeats(90, 130)?.toFixed(2), '0.20');

  // Sign does not matter: late by most of a beat is just as wrong as early by it.
  assert.equal(offsetInBeats(-400, 130)?.toFixed(2), '0.87');
});

test('there is no offset in beats without a tempo to measure it against', () => {
  assert.equal(offsetInBeats(400, null), null);
  assert.equal(offsetInBeats(400, 0), null);
  assert.equal(offsetInBeats(400, Number.NaN), null);
  assert.equal(offsetBeatsLabel(400, null), null);
});

test('an offset far enough into a beat says so, and a small one does not', () => {
  assert.equal(offsetIsNearABeat(400, 130), true);
  assert.equal(offsetIsNearABeat(90, 130), false);
  assert.match(offsetBeatsLabel(400, 130) ?? '', /wrong beat/);
  assert.doesNotMatch(offsetBeatsLabel(90, 130) ?? '', /wrong beat/);
});

test('an offset of nothing has no beat reading, because there is nothing to report', () => {
  assert.equal(offsetBeatsLabel(0, 130), null);
});

test('brightness of nothing reads as off rather than as a percentage', () => {
  // A light at zero is indistinguishable from a stream that never opened, and this
  // label is the only thing that can say which it is.
  assert.equal(brightnessLabel(0), 'off');
  assert.equal(brightnessLabel(60), '60%');
  assert.equal(brightnessLabel(Number.NaN), 'off');
});

test('both ends of the pulse depth are named, because both are a different behaviour', () => {
  assert.equal(pulseDepthLabel(0), 'no dip');
  assert.equal(pulseDepthLabel(100), 'down to black');
  assert.equal(pulseDepthLabel(40), '40%');
});

test('the percentage controls cannot ask for more than the server accepts', () => {
  // Mirrored from LightSettings.MaxPercent.
  assert.equal(PERCENT_LIMIT, 100);
});

test('the pulse slider cannot rest on a length a bulb renders inconsistently', () => {
  // Not "invisible" - aliased. A bulb samples about every 40 ms, so below two samples
  // some beats catch the peak and some catch the tail, which flickers at no setting
  // anybody chose. The control snaps out of the band rather than explaining it.
  assert.equal(MIN_RENDERABLE_PULSE_MS, 80);

  assert.equal(renderablePulseMs(70), 80);
  assert.equal(renderablePulseMs(40), 80);
  assert.equal(renderablePulseMs(30), 0);
  assert.equal(renderablePulseMs(10), 0);
});

test('no pulse stays reachable, because it is a setting rather than a short pulse', () => {
  assert.equal(renderablePulseMs(0), 0);
  assert.equal(renderablePulseMs(-50), 0);
  assert.equal(renderablePulseMs(Number.NaN), 0);
});

test('a pulse long enough to render is left exactly as it was asked for', () => {
  assert.equal(renderablePulseMs(80), 80);
  assert.equal(renderablePulseMs(240), 240);
  assert.equal(renderablePulseMs(2000), 2000);
});
