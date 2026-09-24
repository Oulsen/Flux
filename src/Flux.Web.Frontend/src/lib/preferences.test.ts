import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readStageOn, writeStageOn, type PreferenceStore } from './preferences.ts';

function store(initial: Record<string, string> = {}): PreferenceStore & { items: Record<string, string> } {
  const items = { ...initial };

  return {
    items,
    getItem: (key) => items[key] ?? null,
    setItem: (key, value) => {
      items[key] = value;
    },
  };
}

/** A browser with storage blocked, which throws rather than returning null. */
function hostile(): PreferenceStore {
  return {
    getItem: () => {
      throw new DOMException('The operation is insecure.');
    },
    setItem: () => {
      throw new DOMException('The operation is insecure.');
    },
  };
}

test('a device that has never said otherwise gets the visualizer', () => {
  // The reverse of what this page shipped with, and the whole point of the rebuild: a
  // phone opening Flux for the first time should see the stage, not an empty window.
  assert.equal(readStageOn(store()), true);
  assert.equal(readStageOn(null), true);
});

test('turning the stage off is remembered, and turning it back on clears that', () => {
  const saved = store();

  writeStageOn(saved, false);
  assert.equal(readStageOn(saved), false);

  writeStageOn(saved, true);
  assert.equal(readStageOn(saved), true);
});

test('a value nobody wrote is treated as on rather than as off', () => {
  // Only the exact string means off. Anything else is a key from another version of
  // this page, or a hand-edited value, and the safe reading of "I cannot tell" is the
  // one that still shows somebody their app.
  assert.equal(readStageOn(store({ 'flux.stage': 'nonsense' })), true);
  assert.equal(readStageOn(store({ 'flux.stage': 'off' })), false);
});

test('storage that throws cannot stop the page from starting', () => {
  // localStorage throws on the property access itself when a browser blocks it, which
  // Safari does in private browsing. This runs during startup, so an escaping throw
  // takes the beat display down with it - to remember a preference.
  assert.equal(readStageOn(hostile()), true);
  assert.doesNotThrow(() => writeStageOn(hostile(), false));
});
