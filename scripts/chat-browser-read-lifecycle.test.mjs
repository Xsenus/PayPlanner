// @vitest-environment node

import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { runInNewContext } from 'node:vm';
import { afterEach, test, vi } from 'vitest';
import { createChatPostRecorder, createChatReadLifecycle, drainChatNavigationReads, redactChatBrowserDiagnostic } from './chat-browser-read-lifecycle.mjs';

test('navigation waits for an unfinished notification response even when chat reads are idle', async () => {
  const page = new EventEmitter();
  const chat = createChatReadLifecycle(page, request => request.chat, 20000);
  const api = createChatReadLifecycle(page, request => request.api, 20000);
  const notification = { api: true, chat: false };
  page.emit('request', notification);
  let done = false;
  const navigation = drainChatNavigationReads(chat, api).then(() => { done = true; });
  page.emit('response', notification);
  await Promise.resolve();
  assert.equal(done, false);
  page.emit('requestfinished', notification);
  await navigation;
  assert.equal(done, true);
  chat.dispose(); api.dispose();
});

test('navigation rejects an unexpected failed API read outside the chat', async () => {
  const page = new EventEmitter();
  const chat = createChatReadLifecycle(page, request => request.chat, 20000);
  const api = createChatReadLifecycle(page, request => request.api, 20000);
  const notification = { api: true, chat: false };
  page.emit('request', notification);
  const navigation = drainChatNavigationReads(chat, api);
  page.emit('requestfailed', notification);
  await assert.rejects(navigation, /owned API read failed/);
  assert.equal(api.snapshot().failed, 1);
  chat.dispose(); api.dispose();
});

test('the exact injected chat status abort remains explicit in both navigation observers', async () => {
  const page = new EventEmitter();
  const chat = createChatReadLifecycle(page, request => request.chat, 20000);
  const api = createChatReadLifecycle(page, request => request.api, 20000);
  const status = { api: true, chat: true };
  page.emit('request', status);
  chat.expectLocalAbort(status); api.expectLocalAbort(status);
  const navigation = drainChatNavigationReads(chat, api);
  page.emit('requestfailed', status);
  await navigation;
  assert.equal(chat.snapshot().locallyAborted, 1);
  assert.equal(api.snapshot().locallyAborted, 1);
  assert.equal(api.snapshot().failed, 0);
  chat.dispose(); api.dispose();
});

afterEach(() => vi.useRealTimers());

test('drain waits for full request completion and includes reads started while waiting', async () => {
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, request => request.owned, 20000);
  const first = { owned: true }; const second = { owned: true };
  page.emit('request', first);
  let done = false;
  const drain = reads.drain().then(() => { done = true; });
  page.emit('response', first);
  await Promise.resolve();
  assert.equal(done, false);
  page.emit('request', second);
  page.emit('requestfinished', first);
  await Promise.resolve();
  assert.equal(done, false);
  page.emit('requestfinished', second);
  await drain;
  assert.deepEqual(reads.snapshot(), { started: 2, finished: 2, failed: 0, locallyAborted: 0, pending: 0 });
  reads.dispose();
});

test('failed owned reads are retained separately from successful completion', async () => {
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, request => request.owned, 20000);
  const request = { owned: true };
  page.emit('request', request);
  const drain = reads.drain();
  page.emit('requestfailed', request);
  await drain;
  page.emit('requestfinished', request);
  assert.deepEqual(reads.snapshot(), { started: 1, finished: 0, failed: 1, locallyAborted: 0, pending: 0 });
  reads.dispose();
});

test('only the exact request rejected at the local route boundary is classified as injected loss', async () => {
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, request => request.owned, 20000);
  const local = { owned: true }; const unexpected = { owned: true }; const foreign = { owned: false };
  for (const request of [local, unexpected, foreign]) page.emit('request', request);
  reads.expectLocalAbort(local);
  assert.throws(() => reads.expectLocalAbort(foreign), /exact owned read/);
  for (const request of [local, unexpected, foreign]) page.emit('requestfailed', request);
  await reads.drain();
  assert.deepEqual(reads.snapshot(), { started: 2, finished: 0, failed: 1, locallyAborted: 1, pending: 0 });
  reads.dispose();
});

test('a read stays pending when injection is disabled between its start and route dispatch', async () => {
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, () => true, 20000);
  const request = {};
  let injectLoss = true;
  page.emit('request', request);
  let done = false;
  const drain = reads.drain().then(() => { done = true; });
  injectLoss = false;
  if (injectLoss) reads.expectLocalAbort(request);
  await Promise.resolve();
  assert.equal(done, false);
  page.emit('requestfinished', request);
  await drain;
  assert.deepEqual(reads.snapshot(), { started: 1, finished: 1, failed: 0, locallyAborted: 0, pending: 0 });
  reads.dispose();
});

test('an unfinished read fails at the existing deadline and can later drain', async () => {
  vi.useFakeTimers();
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, () => true, 20000);
  const request = {};
  page.emit('request', request);
  const rejected = assert.rejects(reads.drain(), /did not finish/);
  await vi.advanceTimersByTimeAsync(20000);
  await rejected;
  page.emit('requestfinished', request);
  await reads.drain();
  reads.dispose();
  assert.equal(vi.getTimerCount(), 0);
});

test('dispose removes only its listeners and rejects an active drain', async () => {
  const page = new EventEmitter(); const foreign = () => {};
  page.on('request', foreign);
  const reads = createChatReadLifecycle(page, () => true, 20000);
  page.emit('request', {});
  const rejected = assert.rejects(reads.drain(), /closed with unfinished reads/);
  reads.dispose();
  await rejected;
  assert.deepEqual(page.listeners('request'), [foreign]);
  assert.equal(page.listenerCount('requestfinished'), 0);
  assert.equal(page.listenerCount('requestfailed'), 0);
  await assert.rejects(reads.drain(), /observer is closed/);
});

test('the serialized observer has no module dependency in the Playwright CLI scope', async () => {
  const factory = new Function(`return (${createChatReadLifecycle.toString()});`)();
  const page = new EventEmitter();
  const reads = factory(page, () => true, 20000);
  const request = {};
  page.emit('request', request); page.emit('requestfinished', request);
  await reads.drain();
  assert.equal(reads.describe(), 'started=1 finished=1 failed=0 locallyAborted=0 pending=0');
  reads.dispose();
});

test('diagnostic reads observe startup overlap without waiting or altering requests', () => {
  vi.useFakeTimers();
  const page = new EventEmitter();
  const reads = createChatReadLifecycle(page, request => request.api && request.method === 'GET', 20000);
  const initial = { api: true, method: 'GET' }; const completed = { api: true, method: 'GET' };
  for (const request of [initial, completed, { api: true, method: 'POST' }, { api: false, method: 'GET' }])
    page.emit('request', request);
  page.emit('requestfinished', completed);
  assert.deepEqual(reads.snapshot(), { started: 2, finished: 1, failed: 0, locallyAborted: 0, pending: 1 });
  assert.equal(vi.getTimerCount(), 0);
  reads.dispose();
  assert.equal(page.listenerCount('request'), 0);
});

test('Playwright failures keep the timeout and assertion while removing headers, JWT and fixture secrets', () => {
  const jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.signature';
  const failure = [
    'TimeoutError: route.fetch: Timeout 30000ms exceeded.',
    '  - authorization: Bearer ' + jwt,
    '  - Cookie: session=private-cookie',
    '  - Set-Cookie: renewed=private-renewal',
    '  - Proxy-Authorization: Basic cHJpdmF0ZQ==',
    'standalone=' + jwt,
    'bearer opaque-private-token',
    'fixture@example.local secret-password',
    'Send did not remain pending',
  ].join('\n');
  const safe = redactChatBrowserDiagnostic(failure, ['fixture@example.local', 'secret-password', '', undefined]);
  assert.match(safe, /route.fetch: Timeout 30000ms exceeded/);
  assert.match(safe, /Send did not remain pending/);
  for (const secret of [jwt, 'private-cookie', 'private-renewal', 'cHJpdmF0ZQ==', 'opaque-private-token', 'fixture@example.local', 'secret-password'])
    assert.equal(safe.includes(secret), false);
});

test('serialized POST telemetry runs without performance and clamps a backward wall clock', () => {
  const body = `
    if (typeof performance !== 'undefined') throw new Error('VM unexpectedly provides performance');
    const timings = [];
    const record = (${createChatPostRecorder.toString()})({
      snapshot: () => ({ started: 3, finished: 2, failed: 0, pending: 1 })
    }, timings);
    record('start'); record('end'); record('fail');
    timings;
  `;
  const actual = runInNewContext(body);
  for (const [index, phase] of ['start', 'end', 'fail'].entries())
    assert.match(actual[index], new RegExp('^PP_CHAT_BROWSER_POST phase=' + phase
      + ' elapsedMs=\\d+ apiReadsStarted=3 apiReadsFinished=2 apiReadsFailed=0 apiReadsPending=1$'));
  const backward = runInNewContext('let clock = 1000; Date.now = () => clock -= 100;\n' + body);
  assert.equal(backward.every(line => line.includes(' elapsedMs=0 ')), true);
});
