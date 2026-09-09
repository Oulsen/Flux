import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  clearsPendingSelection,
  resolvePending,
  resolvePendingFields,
  retiresHeldBeat,
} from './storeLogic.ts';

test('nothing is retired when no beat frame is held', () => {
  assert.equal(retiresHeldBeat(null, { beatIndex: 99 }, false), false);
  assert.equal(retiresHeldBeat(null, null, true), false);
});

test('a beat frame survives the ordinary race with the snapshot', () => {
  // The two pumps are independent, so a snapshot on the beat or one behind it is
  // the normal state of affairs, and the beat frame is the fresher fact.
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: 40 }, false), false);
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: 39 }, false), false);
});

test('a snapshot past the held frame retires it, because its successor was dropped', () => {
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: 41 }, false), true);
});

test('a renumbering more than one beat back retires it', () => {
  // A tempo change renumbers the Link timeline by thousands of beats; the sign
  // is whichever way the new session happens to count.
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: 38 }, false), true);
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: -2459 }, false), true);
});

test('a source that has never spoken retires the held position', () => {
  assert.equal(retiresHeldBeat({ index: 40 }, null, false), true);
});

test('a source switch retires the held frame whatever the numbers say', () => {
  // Same index, so the arithmetic would keep it. The numbering belongs to the
  // source that issued it, so across a switch it means nothing.
  assert.equal(retiresHeldBeat({ index: 40 }, { beatIndex: 40 }, true), true);
});

test('a selection stops being pending once a frame confirms it', () => {
  assert.equal(resolvePending('audio', 'audio'), null);
});

test('a selection stays pending while the server still says otherwise', () => {
  assert.equal(resolvePending('audio', 'link'), 'audio');
});

test('nothing pending stays nothing, whatever the server reports', () => {
  assert.equal(resolvePending(null, 'link'), null);
});

test('a confirmed field stops being pending while the others carry on waiting', () => {
  assert.deepEqual(
    resolvePendingFields({ brightness: 60, pulseMs: 120 }, { brightness: 60, pulseMs: 240, palette: 'warm' }),
    { pulseMs: 120 },
  );
});

test('a field nobody asked about is never pending, whatever the server says about it', () => {
  // The case the whole per-field form exists for: another client moved the palette
  // while this one had brightness outstanding, and this one must take their palette.
  assert.deepEqual(resolvePendingFields({ brightness: 60 }, { brightness: 60, palette: 'ocean' }), {});
});

test('an unconfirmed field survives a frame that confirms nothing', () => {
  assert.deepEqual(resolvePendingFields({ palette: 'neon' }, { palette: 'warm' }), { palette: 'neon' });
});

test('only a rejection clears a pending selection', () => {
  assert.equal(clearsPendingSelection('rejected'), true);
  assert.equal(clearsPendingSelection('unknownType'), false);
  assert.equal(clearsPendingSelection('malformed'), false);
});
