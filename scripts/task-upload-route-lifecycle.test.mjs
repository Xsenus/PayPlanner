import assert from 'node:assert/strict';
import test from 'node:test';
import { runTaskUploadRoute } from './task-upload-route-lifecycle.mjs';

test('returns the real route result', async () => {
  assert.equal(await runTaskUploadRoute(async () => 503, () => false, () => assert.fail()), 503);
});

test('a disposed context during the scenario remains a primary failure', async () => {
  const primary = new Error('route.fetch: Request context disposed.');
  await assert.rejects(runTaskUploadRoute(async () => { throw primary; }, () => false, () => assert.fail()),
    error => error === primary);
});

test('a failed backend assertion is not suppressed while closing', async () => {
  const primary = new assert.AssertionError({ actual: 500, expected: 503 });
  await assert.rejects(runTaskUploadRoute(async () => { throw primary; }, () => true, () => assert.fail()),
    error => error === primary);
});

test('only the exact route disposal is recorded during close', async () => {
  let recorded = 0;
  await runTaskUploadRoute(async () => { throw new Error('route.fetch: Request context disposed.\nCall log: owned request'); },
    () => true, () => { recorded++; });
  assert.equal(recorded, 1);
});

test('unrelated errors mentioning disposal are not suppressed', async () => {
  const primary = new Error('Unexpected reply: route.fetch: Request context disposed.');
  await assert.rejects(runTaskUploadRoute(async () => { throw primary; }, () => true, () => assert.fail()),
    error => error === primary);
});

test('a pending route observes the actual start of close', async () => {
  let closing = false;
  let fail;
  let recorded = 0;
  const pending = runTaskUploadRoute(() => new Promise((_, reject) => { fail = reject; }),
    () => closing, () => { recorded++; });
  closing = true;
  fail(new Error('route.fetch: Request context disposed.'));
  await pending;
  assert.equal(recorded, 1);
});
