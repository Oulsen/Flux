import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  beatAt,
  decodeTrack,
  easePlayhead,
  fluxDownbeat,
  isFluxDownbeat,
  peakOver,
  playheadAt,
  rgbOver,
  SNAP_MS,
} from './waveform.ts';

const deck = { number: 2, track: 'abc', positionMs: 1000, rate: 1.05, playing: true };

test('a playing deck carries on at its own rate between frames', () => {
  assert.equal(playheadAt({ deck, receivedAt: 500 }, 600), 1000 + 100 * 1.05);
});

test('a stopped deck stays where it stopped', () => {
  assert.equal(playheadAt({ deck: { ...deck, playing: false }, receivedAt: 500 }, 900), 1000);
});

test('a frame from the future does not run the playhead backwards', () => {
  assert.equal(playheadAt({ deck, receivedAt: 600 }, 500), 1000);
});

test('jitter is eased away and a seek is taken at once', () => {
  const eased = easePlayhead(1000, 1040);
  assert.ok(eased > 1000 && eased < 1040);
  assert.equal(easePlayhead(1000, 1000 + SNAP_MS + 1), 1000 + SNAP_MS + 1);
  assert.equal(easePlayhead(null, 1234), 1234);
});

test('the grid beat at a moment counts from 0 and on past both ends', () => {
  const beats = [100, 600, 1100, 1600];
  assert.equal(beatAt(beats, 100), 0);
  assert.equal(beatAt(beats, 850), 1.5);
  assert.equal(beatAt(beats, 1600), 3);
  assert.equal(beatAt(beats, 2100), 4);
  assert.equal(beatAt(beats, -400), -1);
});

test("Flux's bar 1 is found on the grid from the same frame", () => {
  // The playhead on grid beat 6.02, Flux on beat 2 of its bar at beat 1033.03: Flux's
  // beat 1033 is grid beat 6, so its bar 1 is grid beat 5.
  assert.equal(fluxDownbeat(6.02, 1033.03, 2), 5);
  assert.ok(isFluxDownbeat(9, 5, 4));
  assert.ok(isFluxDownbeat(1, 5, 4));
  assert.ok(!isFluxDownbeat(6, 5, 4));
});

test('a few milliseconds either side of a beat pair the same two beats', () => {
  // The same moment read either side of a beat: the deck just past grid beat 6 while
  // Flux is just short of its beat 1034 (bar 1 on 1033), or the other way round. Both
  // put Flux's bar 1 on grid beat 5.
  assert.equal(fluxDownbeat(6.01, 1033.99, 1), 5);
  assert.equal(fluxDownbeat(5.99, 1034.01, 2), 5);
});

test('a pixel covering several columns shows the peak of each band among them', () => {
  const heights = new Uint8Array([1, 2, 3, 9, 0, 1, 4, 8, 0]);
  assert.deepEqual(peakOver(heights, 0, 3), [9, 8, 3]);
  assert.deepEqual(peakOver(heights, 1.2, 1.4), [9, 0, 1]);
  assert.deepEqual(peakOver(heights, 5, 9), [0, 0, 0]);
});

test('a track decodes both waveforms from base64, and has none where one is null', () => {
  const track = decodeTrack({
    entriesPerSecond: 150,
    threeBand: btoa('\x1e\x0a\x14'),
    rgb: null,
    beatTimesMs: [0],
    beatsInBar: [1],
  });
  assert.deepEqual([...track.lowMidHigh], [30, 10, 20]);
  assert.equal(track.heightRgb.length, 0);
});

test('a track answered before a waveform field existed reads as having none of it', () => {
  const old = { entriesPerSecond: 150, waveform: btoa(''), beatTimesMs: [0, 500], beatsInBar: [1, 2] };
  const track = decodeTrack(old as unknown as Parameters<typeof decodeTrack>[0]);
  assert.equal(track.heightRgb.length, 0);
  assert.equal(track.lowMidHigh.length, 0);
  assert.deepEqual(track.beatTimesMs, [0, 500]);
});

test('an RGB pixel shows the tallest column under it in that column\'s colour', () => {
  const columns = new Uint8Array([4, 7, 0, 0, 20, 0, 0, 7, 9, 0, 7, 0]);
  assert.deepEqual(rgbOver(columns, 0, 3), [20, 0, 0, 7]);
  assert.deepEqual(rgbOver(columns, 2, 2.5), [9, 0, 7, 0]);
  assert.equal(rgbOver(columns, 5, 8), null);
});
