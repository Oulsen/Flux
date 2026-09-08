import assert from 'node:assert/strict';
import { test } from 'node:test';
import { UNKNOWN } from './format.ts';
import {
  advanceMeter,
  attackTowards,
  formatLevel,
  METER_ATTACK_MS,
  METER_FLOOR_DBFS,
  meterFraction,
  METER_RELEASE_PER_SECOND,
  releaseTowards,
  toDbfs,
} from './levels.ts';

test('full scale is 0 dBFS and a tenth of it is 20 dB down', () => {
  assert.equal(toDbfs(1), 0);
  assert.ok(Math.abs(toDbfs(0.1) - -20) < 1e-9, `got ${toDbfs(0.1)}`);
});

test('digital silence is not a small number, it is no number', () => {
  // A bit-exact zero is what a stopped deck and a closed fader both read, and
  // the second of those is the fault that cost this project two recordings.
  assert.equal(toDbfs(0), Number.NEGATIVE_INFINITY);
  assert.equal(toDbfs(-1), Number.NEGATIVE_INFINITY);
  assert.equal(toDbfs(Number.NaN), Number.NEGATIVE_INFINITY);
});

test('the bar runs from the floor to full scale', () => {
  assert.equal(meterFraction(1), 1);
  assert.equal(meterFraction(0), 0);
  // Halfway up the bar is halfway down the decibel range, not half the level.
  assert.ok(Math.abs(meterFraction(10 ** (METER_FLOOR_DBFS / 2 / 20)) - 0.5) < 1e-9);
});

test('a level past full scale pins the bar rather than overflowing it', () => {
  assert.equal(meterFraction(4), 1);
});

test('a level nobody measured is never rendered as a number', () => {
  assert.equal(formatLevel(undefined), UNKNOWN);
  assert.equal(formatLevel(null), UNKNOWN);
});

test('a measured silence says so, and is not the same as an unmeasured one', () => {
  assert.equal(formatLevel(0), 'silent');
  assert.notEqual(formatLevel(0), formatLevel(undefined));
});

test('one time constant covers most of the way up, and never overshoots', () => {
  // The textbook figure for an exponential approach: 1 - 1/e of the gap.
  const after = attackTowards(0, 1, METER_ATTACK_MS);

  assert.ok(Math.abs(after - (1 - Math.exp(-1))) < 1e-9, `one constant landed at ${after}`);
  assert.ok(attackTowards(0, 1, 10_000) <= 1, 'a long frame must not overshoot the target');
});

test('an averaged level is eased on the way up, not snapped to the window', () => {
  // The bug this fixes: a 250 ms window jumping the bar straight to its new
  // value, which twitches at exactly the rate the windows close.
  const eased = advanceMeter(0.2, 0.8, 16);

  assert.ok(eased > 0.2 && eased < 0.8, `a single frame jumped to ${eased}`);
});

test('an averaged level still falls at the release rate', () => {
  const after = advanceMeter(1, 0, 1000);

  assert.ok(Math.abs(after - (1 - METER_RELEASE_PER_SECOND)) < 1e-9, `fell to ${after}`);
});

test('the peak tick rises instantly, because easing a peak understates a clip', () => {
  assert.equal(releaseTowards(0.1, 0.9, 16), 0.9);
  // Even with no time elapsed at all: a transient must not wait for a frame.
  assert.equal(releaseTowards(0.1, 0.9, 0), 0.9);
});

test('the meter falls at a steady rate rather than jumping', () => {
  const after = releaseTowards(1, 0, 1000);

  assert.ok(
    Math.abs(after - (1 - METER_RELEASE_PER_SECOND)) < 1e-9,
    `a second of falling landed at ${after}`,
  );
});

test('the meter never falls past what was measured', () => {
  // A whole minute of release cannot take the bar below the current reading,
  // which is what stops a held level from sinking into the floor.
  assert.equal(releaseTowards(0.5, 0.4, 60_000), 0.4);
});

test('time that did not pass does not move the meter down', () => {
  assert.equal(releaseTowards(0.5, 0.1, 0), 0.5);
  assert.equal(releaseTowards(0.5, 0.1, Number.NaN), 0.5);
});

test('a measured level is shown in decibels to one place', () => {
  assert.equal(formatLevel(1), '0.0 dB');
  assert.equal(formatLevel(0.1), '-20.0 dB');
});
