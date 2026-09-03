import assert from 'node:assert/strict';
import { test } from 'node:test';
import { reconnectDelay } from './backoff.ts';
import {
  alignmentLabel,
  formatAgo,
  formatBpm,
  formatCount,
  formatDrift,
  formatLatency,
  formatRate,
  UNKNOWN,
} from './format.ts';
import { parseMessage } from './protocol.ts';

test('a tempo is always shown to two decimals', () => {
  assert.equal(formatBpm(128), '128.00');
  assert.equal(formatBpm(174.5), '174.50');
});

test('an absent tempo is a placeholder, never a zero', () => {
  assert.equal(formatBpm(null), UNKNOWN);
  assert.equal(formatBpm(undefined), UNKNOWN);
  assert.equal(formatBpm(Number.NaN), UNKNOWN);
});

test('re-anchor drift keeps four decimals, because it is watched against zero', () => {
  assert.equal(formatDrift(0), '0.0000');
  assert.equal(formatDrift(0.00004), '0.0000');
  assert.equal(formatDrift(0.25), '0.2500');
});

test('a round trip is whole milliseconds and never negative', () => {
  assert.equal(formatLatency(3.4), '3 ms');
  assert.equal(formatLatency(-2), '0 ms');
  assert.equal(formatLatency(null), UNKNOWN);
});

test('a counter is whole, and a zero is a reading rather than an absence', () => {
  assert.equal(formatCount(0), '0');
  assert.equal(formatCount(4821), '4821');
  assert.equal(formatCount(3.9), '3');
  assert.equal(formatCount(undefined), UNKNOWN);
  assert.equal(formatCount(Number.NaN), UNKNOWN);
});

test('a measured rate keeps one decimal, since the question is how far under', () => {
  assert.equal(formatRate(60), '60.0');
  assert.equal(formatRate(59.94), '59.9');
  assert.equal(formatRate(undefined), UNKNOWN);
});

test('an elapsed time switches to seconds at ten, and never runs backwards', () => {
  assert.equal(formatAgo(9999), '9999 ms');
  assert.equal(formatAgo(10_000), '10.0 s');
  assert.equal(formatAgo(-5), '0 ms');
  assert.equal(formatAgo(undefined), UNKNOWN);
});

test('an anchored bar says what anchored it', () => {
  // Which one it was is the whole value: a cue press that never arrives looks
  // exactly like a tap that did, unless the two are named apart.
  assert.equal(alignmentLabel(true, 'cue'), 'bar anchored by a cue press');
  assert.equal(alignmentLabel(true, 'tap'), 'bar anchored by a tap');
  assert.equal(alignmentLabel(true, 'drop'), 'bar anchored by a drop');
});

test('an unanchored bar never claims a bar number', () => {
  // The count is right and its starting point is arbitrary, so the reading has
  // to say so however it was reached.
  assert.equal(alignmentLabel(false, null), 'counting beats - bar not anchored yet');
  assert.equal(alignmentLabel(false, 'tap'), 'counting beats - bar not anchored yet');
  assert.equal(alignmentLabel(true, null), 'bar anchored');
});

test('the reconnect delay doubles and then stops growing', () => {
  // Jitter puts each delay between half a step and the full step, so the
  // assertions pin the envelope rather than a point.
  const steps: Array<[attempt: number, step: number]> = [
    [0, 500],
    [1, 1000],
    [2, 2000],
    [20, 10_000],
  ];

  for (const [attempt, step] of steps) {
    const delay = reconnectDelay(attempt);
    assert.ok(delay >= step / 2 && delay <= step, `attempt ${attempt} waited ${delay} ms`);
  }
});

test('a frame is read as far as its type tag', () => {
  const message = parseMessage('{"type":"beat","data":{"index":7}}');

  assert.equal(message?.type, 'beat');
  assert.deepEqual(message?.data, { index: 7 });
});

test('anything that is not a tagged object is not a message', () => {
  assert.equal(parseMessage('not json'), null);
  assert.equal(parseMessage('[]'), null);
  assert.equal(parseMessage('null'), null);
  assert.equal(parseMessage('{"data":{}}'), null);
  assert.equal(parseMessage('{"type":42}'), null);
});
