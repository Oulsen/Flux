import assert from 'node:assert/strict';
import { test } from 'node:test';
import { qrModules, qrPath } from './qr.ts';

const URL = 'http://192.168.1.163:5050/';

test('a phone address fits a small code', () => {
  const modules = qrModules(URL);

  // Version 2 or 3 at level M: 25 or 29 modules a side. Anything larger means the
  // level or the mode changed, and the code got harder to read from across a booth.
  assert.ok(modules.length === 25 || modules.length === 29, `got ${modules.length}`);
  assert.ok(modules.every((row) => row.length === modules.length));
});

test('the three finder patterns sit in their corners', () => {
  const modules = qrModules(URL);
  const end = modules.length - 7;

  // The outer ring of each finder is dark and the ring inside it light.
  for (const [top, left] of [
    [0, 0],
    [0, end],
    [end, 0],
  ] as const) {
    assert.equal(modules[top]![left]!, true);
    assert.equal(modules[top + 6]![left + 6]!, true);
    assert.equal(modules[top + 1]![left + 1]!, false);
    assert.equal(modules[top + 3]![left + 3]!, true);
  }
});

test('the same address always draws the same code', () => {
  assert.deepEqual(qrModules(URL), qrModules(URL));
});

test('the path has one square per dark module', () => {
  const modules = qrModules(URL);
  const dark = modules.flat().filter(Boolean).length;

  assert.equal(qrPath(modules).split('z').length - 1, dark);
});
