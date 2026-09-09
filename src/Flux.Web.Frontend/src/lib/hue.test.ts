import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  areaSummary,
  bridgeStatusLabel,
  canDiscover,
  countdownLabel,
  LATENCY_OFFSET_LIMIT_MS,
  offsetLabel,
  showsPairing,
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
  assert.equal(bridgeStatusLabel(connection({ state: 'ready' })), 'choose an entertainment area');
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
