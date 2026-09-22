import assert from 'node:assert/strict';
import { test } from 'node:test';
import { STROBE_RENEW_MS, StrobeHold, type HoldTimers } from './strobe.ts';

/** Timers that only fire when told to, so a test says exactly how much time passed. */
function fakeTimers(): HoldTimers & { tick(): void; running: number } {
  const callbacks = new Map<number, () => void>();
  let next = 1;

  return {
    setInterval(callback, ms) {
      assert.equal(ms, STROBE_RENEW_MS);
      callbacks.set(next, callback);
      return next++;
    },
    clearInterval(handle) {
      callbacks.delete(handle as number);
    },
    tick() {
      for (const callback of callbacks.values()) {
        callback();
      }
    },
    get running() {
      return callbacks.size;
    },
  };
}

test('a press is said at once and repeated for as long as it lasts', () => {
  const timers = fakeTimers();
  const sent: boolean[] = [];
  const hold = new StrobeHold((held) => sent.push(held), timers);

  hold.press();
  timers.tick();
  timers.tick();

  assert.deepEqual(sent, [true, true, true]);
  assert.equal(hold.held, true);
});

test('a release says so once and stops the repeating', () => {
  const timers = fakeTimers();
  const sent: boolean[] = [];
  const hold = new StrobeHold((held) => sent.push(held), timers);

  hold.press();
  hold.release();
  timers.tick();

  assert.deepEqual(sent, [true, false]);
  assert.equal(timers.running, 0);
  assert.equal(hold.held, false);
});

test('every way a press can end may fire, and only the first says anything', () => {
  const timers = fakeTimers();
  const sent: boolean[] = [];
  const hold = new StrobeHold((held) => sent.push(held), timers);

  hold.press();
  hold.press();
  hold.release();
  hold.release();

  assert.deepEqual(sent, [true, false]);
  assert.equal(timers.running, 0);
});

test('releasing a button nobody pressed sends nothing', () => {
  const sent: boolean[] = [];
  const hold = new StrobeHold((held) => sent.push(held), fakeTimers());

  hold.release();

  assert.deepEqual(sent, []);
});
