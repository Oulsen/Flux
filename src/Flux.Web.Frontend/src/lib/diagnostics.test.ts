import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sourceRows, stagePhrase } from './diagnostics.ts';
import { UNKNOWN } from './format.ts';
import type { DiagnosticsData } from './protocol.ts';

/** The only field every source carries, and the grid's rather than a source's. */
const GRID: DiagnosticsData = { reanchorMaxBeats: 0 };

function labels(source: string, diagnostics: DiagnosticsData): string[] {
  return sourceRows(source, diagnostics).map((row) => row.label);
}

function valueOf(source: string, diagnostics: DiagnosticsData, label: string): string | undefined {
  return sourceRows(source, diagnostics).find((row) => row.label === label)?.value;
}

test('each source is asked only for the counters it keeps', () => {
  assert.deepEqual(labels('link', GRID), ['packets']);
  assert.deepEqual(labels('audio', GRID), ['fps', 'beats', 'bpm lock', 'last beat']);
  assert.deepEqual(labels('manual', GRID), ['taps', 'last tap']);
  assert.deepEqual(labels('rekordbox', GRID), ['deck', 'status', 'phrase']);
});

test('rekordbox says in words why it is not following, and nothing when it is', () => {
  const stopped: DiagnosticsData = { reanchorMaxBeats: 0, deck: 2, detail: 'deck 2 is stopped' };
  assert.equal(valueOf('rekordbox', stopped, 'deck'), '2');
  assert.equal(valueOf('rekordbox', stopped, 'status'), 'deck 2 is stopped');
  assert.equal(valueOf('rekordbox', { reanchorMaxBeats: 0, deck: 1 }, 'status'), 'following');
  assert.equal(valueOf('rekordbox', GRID, 'deck'), UNKNOWN);
});

test('rekordbox names the phrase playing, or why there is none, as the server words it', () => {
  assert.equal(valueOf('rekordbox', { reanchorMaxBeats: 0, deck: 1, phrase: 'Chorus 2' }, 'phrase'), 'Chorus 2');
  assert.equal(
    valueOf('rekordbox', { reanchorMaxBeats: 0, deck: 1, phrase: 'none: the phrases do not fit the beatgrid' }, 'phrase'),
    'none: the phrases do not fit the beatgrid',
  );
  assert.equal(valueOf('rekordbox', GRID, 'phrase'), UNKNOWN);
});

test('no source is offered another source\'s counter', () => {
  // The defect this replaces: a packets row, which only Link has, rendered
  // under an AUDIO heading against a value that was correctly blank.
  assert.ok(!labels('audio', GRID).includes('packets'));
  assert.ok(!labels('manual', GRID).includes('packets'));
  assert.ok(!labels('link', GRID).includes('beats'));
});

test('a source this page has never heard of contributes no rows', () => {
  // The protocol's documented degradation: a newer server may name a source
  // this build predates, and the grid's own metrics still have to render.
  assert.deepEqual(sourceRows('turntable', GRID), []);
  assert.deepEqual(sourceRows('', GRID), []);
});

test('a counter the source has not reported reads as a blank, never a zero', () => {
  assert.equal(valueOf('link', GRID, 'packets'), UNKNOWN);
  assert.equal(valueOf('audio', GRID, 'fps'), UNKNOWN);
  assert.equal(valueOf('audio', GRID, 'beats'), UNKNOWN);
  assert.equal(valueOf('manual', GRID, 'taps'), UNKNOWN);
});

test('the counters render as measurements once they arrive', () => {
  const audio: DiagnosticsData = {
    reanchorMaxBeats: 0,
    fps: 59.94,
    beatsDetected: 312,
    bpmLocked: true,
    lastBeatAgoMs: 461.7,
  };

  assert.equal(valueOf('audio', audio, 'fps'), '59.9');
  assert.equal(valueOf('audio', audio, 'beats'), '312');
  assert.equal(valueOf('audio', audio, 'bpm lock'), 'locked');
  assert.equal(valueOf('audio', audio, 'last beat'), '462 ms');
});

test('a tempo that has not settled says so, and an absent one says nothing', () => {
  // False is a real report from the detector and blank is the absence of one,
  // so the two must not collapse into the same cell.
  assert.equal(valueOf('audio', { ...GRID, bpmLocked: false }, 'bpm lock'), 'settling');
  assert.equal(valueOf('audio', { ...GRID, bpmLocked: true }, 'bpm lock'), 'locked');
  assert.equal(valueOf('audio', GRID, 'bpm lock'), UNKNOWN);
});

test('a count of zero is a measurement and is shown as one', () => {
  // Nothing detected yet is a fact worth reading. Only an absent field blanks.
  assert.equal(valueOf('audio', { ...GRID, beatsDetected: 0 }, 'beats'), '0');
  assert.equal(valueOf('manual', { ...GRID, taps: 0 }, 'taps'), '0');
});

test('an elapsed time leaves milliseconds behind once they stop meaning anything', () => {
  // A tap nobody has repeated for a minute would otherwise be five digits of
  // noise in a cell sized for three.
  assert.equal(valueOf('manual', { ...GRID, lastTapAgoMs: 940 }, 'last tap'), '940 ms');
  assert.equal(valueOf('manual', { ...GRID, lastTapAgoMs: 62_500 }, 'last tap'), '62.5 s');
});

test('the stage names a phrase and never the reason there is none', () => {
  assert.equal(stagePhrase('rekordbox', { reanchorMaxBeats: 0, deck: 1, phrase: 'Chorus 2' }), 'Chorus 2');
  assert.equal(stagePhrase('rekordbox', { reanchorMaxBeats: 0, deck: 1, phrase: 'none: the phrases do not fit the beatgrid' }), null);
  assert.equal(stagePhrase('rekordbox', GRID), null);
  assert.equal(stagePhrase('rekordbox', null), null);
});

test('the stage names no phrase for a source that does not offer them', () => {
  assert.equal(stagePhrase('link', { reanchorMaxBeats: 0, phrase: 'Chorus 2' }), null);
});
