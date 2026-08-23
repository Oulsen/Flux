import assert from 'node:assert/strict';
import { test } from 'node:test';
import { reconnectDelay } from './backoff.ts';
import { formatBpm, formatDrift, formatLatency, UNKNOWN } from './format.ts';
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

test('the reconnect delay doubles and then stops growing', () => {
  assert.equal(reconnectDelay(0), 500);
  assert.equal(reconnectDelay(1), 1000);
  assert.equal(reconnectDelay(2), 2000);
  assert.equal(reconnectDelay(20), 10_000);
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
