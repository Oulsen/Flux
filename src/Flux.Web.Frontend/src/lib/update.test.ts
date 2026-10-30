import assert from 'node:assert/strict';
import { test } from 'node:test';
import { canCheckForUpdate, canRestartToUpdate, updateLabel } from './update.ts';

test('a server that cannot update shows nothing', () => {
  assert.equal(updateLabel(null), null);
  assert.equal(updateLabel({ status: 'unavailable', version: null }), null);
  assert.equal(canCheckForUpdate(null), false);
  assert.equal(canRestartToUpdate(null), false);
});

test('a ready version is named and offers the restart', () => {
  const ready = { status: 'ready', version: '0.7.0' } as const;

  assert.equal(updateLabel(ready), 'flux 0.7.0 is ready');
  assert.equal(canRestartToUpdate(ready), true);
  assert.equal(canCheckForUpdate(ready), false);
});

test('a check is offered after an answer and not during one', () => {
  assert.equal(canCheckForUpdate({ status: 'current', version: null }), true);
  assert.equal(canCheckForUpdate({ status: 'failed', version: null }), true);
  assert.equal(canCheckForUpdate({ status: 'checking', version: null }), false);
});
