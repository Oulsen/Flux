import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { SpectrumBeat, SpectrumData } from './protocol.ts';
import {
  advanceVisual,
  decayFactor,
  emptyVisual,
  flashFor,
  FLASH_DECAY_PER_FRAME,
  LEVEL_DECAY_PER_FRAME,
  normalizeBin,
  receiveFrame,
  REFERENCE_FPS,
} from './spectrum.ts';

/** One frame at the rate the scene's constants were written for. */
const FRAME_MS = 1000 / REFERENCE_FPS;

function frame(bins: number[], beat: SpectrumBeat = 'none'): SpectrumData {
  return { bins, kick: 0, mids: 0, highs: 0, flux: 0, beat };
}

function assertClose(actual: number, expected: number, what: string): void {
  assert.ok(Math.abs(actual - expected) < 1e-9, `${what}: expected ${expected}, got ${actual}`);
}

test('a spectrum byte becomes a level from 0 to 1', () => {
  assert.equal(normalizeBin(0), 0);
  assert.equal(normalizeBin(255), 1);
  assertClose(normalizeBin(128), 128 / 255, 'half scale');
});

test('a level outside the byte scale is clamped rather than believed', () => {
  assert.equal(normalizeBin(-5), 0);
  assert.equal(normalizeBin(400), 1);
  assert.equal(normalizeBin(Number.NaN), 0);
});

test('the flash is set by what the detector made of the frame', () => {
  assert.equal(flashFor('none'), 0);
  assert.equal(flashFor('beat'), 0.5);
  assert.equal(flashFor('strong'), 1);
});

test('one frame at the reference rate decays by exactly the scene constant', () => {
  assertClose(decayFactor(LEVEL_DECAY_PER_FRAME, FRAME_MS), LEVEL_DECAY_PER_FRAME, 'level');
  assertClose(decayFactor(FLASH_DECAY_PER_FRAME, FRAME_MS), FLASH_DECAY_PER_FRAME, 'flash');
});

test('the decay follows the clock, not the redraw, so a faster display matches', () => {
  // Two frames' worth of time decays by the square whether it arrived as one
  // frame or two, which is what keeps a 144 Hz monitor from falling faster.
  const once = decayFactor(LEVEL_DECAY_PER_FRAME, FRAME_MS * 2);
  const twice = decayFactor(LEVEL_DECAY_PER_FRAME, FRAME_MS) ** 2;

  assertClose(once, twice, 'two frames');
  assertClose(once, LEVEL_DECAY_PER_FRAME ** 2, 'against the constant');
});

test('time that did not pass decays nothing', () => {
  // Two redraws can share a timestamp, and a nonsense delta must not eat a frame.
  assert.equal(decayFactor(LEVEL_DECAY_PER_FRAME, 0), 1);
  assert.equal(decayFactor(LEVEL_DECAY_PER_FRAME, -8), 1);
  assert.equal(decayFactor(LEVEL_DECAY_PER_FRAME, Number.NaN), 1);
});

test('a new visualizer holds nothing at all', () => {
  const visual = emptyVisual(3);

  assert.deepEqual(visual.targets, [0, 0, 0]);
  assert.deepEqual(visual.levels, [0, 0, 0]);
  assert.equal(visual.flash, 0);
});

test('a frame becomes the target levels', () => {
  const visual = receiveFrame(emptyVisual(2), frame([255, 0]));

  assert.deepEqual(visual.targets, [1, 0]);
});

test('the flash rises on a frame and never falls on one', () => {
  const lit = { ...emptyVisual(1), flash: 0.9 };

  assert.equal(receiveFrame(lit, frame([0], 'strong')).flash, 1);
  // A soft beat must not cut the tail off the strong one before it.
  assert.equal(receiveFrame(lit, frame([0], 'beat')).flash, 0.9);
  assert.equal(receiveFrame(lit, frame([0], 'none')).flash, 0.9);
});

test('a frame of a different width restarts the levels from the measurement', () => {
  // Smoothing across a resize would carry one bar's history onto another band.
  const held = { targets: [0.4, 0.4], levels: [0.4, 0.4], flash: 0 };
  const visual = receiveFrame(held, frame([255, 0, 0]));

  assert.deepEqual(visual.levels, [1, 0, 0]);
});

test('a bar reaches its full height on the frame the kick lands', () => {
  const visual = advanceVisual({ targets: [1], levels: [0], flash: 0 }, FRAME_MS);

  assert.equal(visual.levels[0], 1);
});

test('a held level does not shimmer', () => {
  // The scene's own comparison decays a level sitting exactly on its
  // measurement and then snaps it back, which is a wobble the music is not
  // making. This one includes equality precisely so it cannot.
  let visual = { targets: [0.6], levels: [0.6], flash: 0 };

  for (let redraw = 0; redraw < 10; redraw++) {
    visual = advanceVisual(visual, FRAME_MS);
  }

  assert.equal(visual.levels[0], 0.6);
});

test('a falling level releases toward the new measurement and stops there', () => {
  let visual = { targets: [0.5], levels: [1], flash: 0 };

  visual = advanceVisual(visual, FRAME_MS);
  assertClose(visual.levels[0]!, LEVEL_DECAY_PER_FRAME, 'after one frame');

  // 0.95^n crosses 0.5 at fourteen frames; well past that it must sit on the
  // measurement rather than carry on toward zero.
  for (let redraw = 0; redraw < 30; redraw++) {
    visual = advanceVisual(visual, FRAME_MS);
  }

  assert.equal(visual.levels[0], 0.5);
});

test('the flash decays and is gone within a second', () => {
  let visual = { targets: [0], levels: [0], flash: 1 };

  visual = advanceVisual(visual, FRAME_MS);
  assertClose(visual.flash, FLASH_DECAY_PER_FRAME, 'after one frame');

  visual = advanceVisual(visual, 1000);
  assert.ok(visual.flash < 0.001, `a second later the flash was still ${visual.flash}`);
});
