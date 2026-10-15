// @vitest-environment node

import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { runInNewContext } from 'node:vm';
import { describe, it } from 'vitest';
import { createCaseReadLifecycle, createCaseStartupReadTrace, isCaseBoundaryRead } from './case-retirement-browser-lifecycle.mjs';

describe('startup request diagnostics', () => {
  it('observes completion, pending reads and failures without serializing URLs or payloads', () => {
    const page = new EventEmitter();
    const create = runInNewContext('(' + createCaseStartupReadTrace.toString() + ')', { URL });
    const diagnostics = create(page, 'http://127.0.0.1:1234', 7);
    const request = path => ({ url: () => 'http://127.0.0.1:1234' + path + '?token=synthetic-private', method: () => 'GET' });
    const completed = request('/api/v2/cases/7');
    const pending = request('/api/notifications/inbox');
    const failed = request('/api/v2/cases/appeal-deadlines/expiring');
    for (const value of [completed, pending, failed]) page.emit('request', value);
    page.emit('response', { request: () => completed, status: () => 200 });
    page.emit('requestfinished', completed);
    page.emit('requestfailed', failed);
    const snapshot = JSON.parse(JSON.stringify(diagnostics.snapshot()));
    assert.deepEqual(snapshot.map(({ kind, status, outcome }) => ({ kind, status, outcome })), [
      { kind: 'case-details', status: 200, outcome: 'finished' },
      { kind: 'inbox', status: null, outcome: 'pending' },
      { kind: 'appeal-deadlines', status: null, outcome: 'failed' },
    ]);
    assert.ok(snapshot.every(value => value.elapsedMs >= 0 && value.elapsedMs <= 3_600_000));
    assert.ok(!JSON.stringify(snapshot).includes('synthetic-private'));
    diagnostics.dispose();
    assert.equal(page.listenerCount('request'), 0);
  });

  it('ignores external requests and writes and bounds a long request stream', () => {
    const page = new EventEmitter();
    const diagnostics = createCaseStartupReadTrace(page, 'http://127.0.0.1:1234', 7);
    page.emit('request', { url: () => 'https://outside.invalid/private', method: () => 'GET' });
    page.emit('request', { url: () => 'http://127.0.0.1:1234/api/private', method: () => 'POST' });
    assert.equal(diagnostics.snapshot().length, 0);
    for (let index = 0; index < 100; index++)
      page.emit('request', { url: () => 'http://127.0.0.1:1234/api/private/' + index, method: () => 'GET' });
    assert.equal(diagnostics.snapshot().length, 64);
    assert.ok(diagnostics.snapshot().every(value => value.kind === 'other-read'));
    diagnostics.dispose();
  });
});

function deferred() {
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  return { promise, resolve };
}

function fixture() {
  const page = new EventEmitter();
  page.evaluate = async () => {};
  const timers = new Set();
  // Execute the actual serialized functions with the CLI's minimal globals.
  // No performance object, module imports, clock wait or browser is supplied.
  const create = runInNewContext('(' + createCaseReadLifecycle.toString() + ')', {
    setTimeout: callback => { timers.add(callback); return callback; },
    clearTimeout: callback => timers.delete(callback),
  });
  const predicate = runInNewContext('(' + isCaseBoundaryRead.toString() + ')', { URL });
  const lifecycle = create(page, request => predicate(request, 'http://127.0.0.1:1234', 7), 20000);
  const request = (path = '/api/tasks/settings', method = 'GET', status = 200) => {
    const headers = deferred();
    const body = deferred();
    const value = { url: () => 'http://127.0.0.1:1234' + path, method: () => method, response: () => headers.promise };
    return {
      value,
      headers: () => headers.resolve({ status: () => status, finished: () => body.promise }),
      missing: () => headers.resolve(null),
      finish: () => { body.resolve(null); page.emit('requestfinished', value); },
      failBody: () => { body.resolve(new Error('owned response failure')); page.emit('requestfailed', value); },
    };
  };
  return { page, lifecycle, request, timers };
}

describe('owned case read boundary', () => {
  it.each(['document-tab', 'document-upload', 'browser-close'])('finishes a pending upload response before %s', async phase => {
    const { page, lifecycle, request } = fixture();
    const pending = request('/api/v2/cases/7/documents/uploads/pending');
    page.emit('request', pending.value);
    let complete = false;
    const drain = lifecycle.drain(phase).then(() => { complete = true; });
    pending.headers();
    await Promise.resolve();
    assert.equal(complete, false);
    pending.finish();
    await drain;
    assert.equal(complete, true);
    lifecycle.dispose();
  });

  it.each(['document-tab', 'document-upload', 'browser-close'])('retains an upload response failure before %s', async phase => {
    const { page, lifecycle, request } = fixture();
    const pending = request('/api/v2/cases/7/documents/uploads/pending');
    page.emit('request', pending.value);
    const drain = lifecycle.drain(phase);
    pending.headers();
    pending.failBody();
    await assert.rejects(drain, /response body failed/);
    lifecycle.dispose();
  });

  it('waits for an existing API read and its body before permitting restart', async () => {
    const { page, lifecycle, request } = fixture();
    const settings = request();
    page.emit('request', settings.value);
    let complete = false;
    const drain = lifecycle.drain('server-restart').then(() => { complete = true; });
    await Promise.resolve();
    assert.equal(complete, false);
    settings.headers();
    await Promise.resolve();
    assert.equal(complete, false);
    settings.finish();
    await drain;
    assert.equal(complete, true);
    assert.equal(lifecycle.snapshot().pending, 0);
    lifecycle.dispose();
  });

  it('also waits for a read issued while the first response is settling', async () => {
    const { page, lifecycle, request } = fixture();
    const firstFrame = deferred();
    page.evaluate = async () => { firstFrame.resolve(); };
    const first = request();
    const second = request('/api/notifications/inbox');
    page.emit('request', first.value);
    let complete = false;
    const drain = lifecycle.drain('client-navigation').then(() => { complete = true; });
    first.headers();
    page.emit('request', second.value);
    first.finish();
    await firstFrame.promise;
    assert.equal(complete, false);
    second.headers();
    second.finish();
    await drain;
    assert.equal(complete, true);
    lifecycle.dispose();
  });

  it('does not accept a failed activity write at the existing navigation boundary', async () => {
    const { page, lifecycle, request } = fixture();
    const activity = request('/api/v2/cases/7/activity', 'POST', 503);
    page.emit('request', activity.value);
    const drain = lifecycle.drain('client-navigation');
    activity.headers();
    activity.finish();
    await assert.rejects(drain, /activity write must succeed/);
    lifecycle.dispose();
  });

  it('preserves a request failure before response headers', async () => {
    const { page, lifecycle, request } = fixture();
    const settings = request();
    page.emit('request', settings.value);
    const drain = lifecycle.drain('server-restart');
    settings.missing();
    page.emit('requestfailed', settings.value);
    await assert.rejects(drain, /read failed before server-restart/);
    lifecycle.dispose();
  });

  it('preserves a failed response body after successful headers', async () => {
    const { page, lifecycle, request } = fixture();
    const settings = request();
    page.emit('request', settings.value);
    const drain = lifecycle.drain('server-restart');
    settings.headers();
    settings.failBody();
    await assert.rejects(drain, /response body failed/);
    lifecycle.dispose();
  });

  it('retains a newly started read failure during the render-frame barrier', async () => {
    const { page, lifecycle, request } = fixture();
    const frame = deferred();
    page.evaluate = () => frame.promise;
    const drain = lifecycle.drain('server-restart');
    await Promise.resolve();
    const settings = request();
    page.emit('request', settings.value);
    page.emit('requestfailed', settings.value);
    frame.resolve();
    await assert.rejects(drain, /read was interrupted/);
    lifecycle.dispose();
  });

  it('keeps the original bounded deadline when a read never finishes', async () => {
    const { page, lifecycle, request, timers } = fixture();
    page.emit('request', request().value);
    const drain = lifecycle.drain('server-restart');
    assert.equal(timers.size, 1);
    [...timers][0]();
    await assert.rejects(drain, /within 20000ms/);
    assert.equal(timers.size, 0);
    lifecycle.dispose();
  });

  it('shares the same deadline with initial connection readiness and stops after timeout', async () => {
    const { page, lifecycle, timers } = fixture();
    const initialConnection = deferred();
    let frames = 0;
    page.evaluate = async () => { frames++; };
    const drain = lifecycle.drain('server-restart', () => initialConnection.promise);
    assert.equal(timers.size, 1);
    [...timers][0]();
    await assert.rejects(drain, /within 20000ms/);
    initialConnection.resolve();
    await Promise.resolve();
    assert.equal(frames, 0);
    assert.equal(timers.size, 0);
    lifecycle.dispose();
  });

  it('observes only owned API reads and activity, never the retirement POST itself', () => {
    const { page, lifecycle, request } = fixture();
    page.emit('request', request('/api/v2/cases/7/documents/uploads/key/retire', 'POST').value);
    page.emit('request', request('/api/v2/cases/8/activity', 'POST').value);
    page.emit('request', { url: () => 'http://127.0.0.1:4321/api/tasks/settings', method: () => 'GET' });
    assert.equal(lifecycle.snapshot().pending, 0);
    page.emit('request', request().value);
    page.emit('request', request('/api/v2/cases/7/activity', 'POST').value);
    assert.equal(lifecycle.snapshot().pending, 2);
    lifecycle.dispose();
    assert.equal(page.listenerCount('request'), 0);
    assert.equal(page.listenerCount('requestfinished'), 0);
    assert.equal(page.listenerCount('requestfailed'), 0);
  });
});

import { expect, test } from 'vitest';
import vm from 'node:vm';
import { createCaseRestartObserver } from './case-retirement-browser-lifecycle.mjs';

const api = 'http://127.0.0.1:41234';
const handshake = '{}\u001e';
const socket = (url = 'ws://127.0.0.1:41234/hubs/notifications') => {
  let closed = false;
  return { url: () => url, isClosed: () => closed, close: () => { closed = true; } };
};
const close = (observer, connection) => { connection.close(); observer.onClose(connection); };
const established = onProof => {
  const observer = createCaseRestartObserver(api, onProof);
  const previous = socket();
  expect(observer.observeSocket(previous)).toBe(true);
  observer.onHandshake(previous, handshake);
  expect(observer.snapshot().initialReady).toBe(true);
  observer.beginRestart();
  return { observer, previous };
};
const connect = observer => {
  const next = socket();
  expect(observer.observeSocket(next)).toBe(true);
  return next;
};

test('retains a new live handshake received before the control acknowledgement', () => {
  let notifications = 0;
  const { observer, previous } = established(() => notifications++);
  close(observer, previous);
  observer.onHandshake(connect(observer), handshake);
  expect(observer.hasProof()).toBe(false);
  expect(notifications).toBe(0);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(true);
  expect(notifications).toBe(1);
});

test('accepts a new live handshake received after the control acknowledgement', () => {
  const { observer, previous } = established();
  close(observer, previous);
  const next = connect(observer);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(false);
  observer.onHandshake(next, handshake);
  expect(observer.hasProof()).toBe(true);
});

test('never uses a pre-restart connection frame as reconnect proof', () => {
  const { observer, previous } = established();
  observer.acknowledgeRestart();
  observer.onHandshake(previous, handshake);
  expect(observer.hasProof()).toBe(false);
  close(observer, previous);
  observer.onHandshake(previous, handshake);
  expect(observer.hasProof()).toBe(false);
});

test('rejects a candidate born before the previous connection closed', () => {
  const { observer, previous } = established();
  const tooEarly = connect(observer);
  observer.onHandshake(tooEarly, handshake);
  close(observer, previous);
  observer.acknowledgeRestart();
  observer.onHandshake(tooEarly, handshake);
  expect(observer.hasProof()).toBe(false);
  observer.onHandshake(connect(observer), handshake);
  expect(observer.hasProof()).toBe(true);
});

test('rejects a candidate that closed before the acknowledgement', () => {
  const { observer, previous } = established();
  close(observer, previous);
  const next = connect(observer);
  observer.onHandshake(next, handshake);
  close(observer, next);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(false);
});

test('revokes proof as soon as the socket reports closed even before close delivery', () => {
  const { observer, previous } = established();
  close(observer, previous);
  const next = connect(observer);
  observer.onHandshake(next, handshake);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(true);
  next.close();
  expect(observer.hasProof()).toBe(false);
  observer.onClose(next);
  expect(observer.hasProof()).toBe(false);
});

test('does not infer a handshake from an open socket or an unrelated frame', () => {
  const { observer, previous } = established();
  close(observer, previous);
  const next = connect(observer);
  observer.acknowledgeRestart();
  observer.onHandshake(next, '{"type":6}\u001e');
  observer.onHandshake(next, '{}');
  observer.onHandshake(next, null);
  expect(observer.hasProof()).toBe(false);
});

test.each([
  'ws://127.0.0.1:41235/hubs/notifications',
  'ws://127.0.0.1:41234/hubs/other',
  'http://127.0.0.1:41234/hubs/notifications',
])('ignores a foreign socket: %s', url => {
  const { observer, previous } = established();
  close(observer, previous);
  const foreign = socket(url);
  expect(observer.observeSocket(foreign)).toBe(false);
  observer.onHandshake(foreign, handshake);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(false);
});

test('requires closure of every live pre-restart socket including one without a handshake', () => {
  const observer = createCaseRestartObserver(api);
  const first = connect(observer);
  const second = connect(observer);
  observer.onHandshake(first, handshake);
  observer.beginRestart();
  close(observer, first);
  const beforeAllClosed = connect(observer);
  observer.onHandshake(beforeAllClosed, handshake);
  close(observer, second);
  observer.acknowledgeRestart();
  expect(observer.snapshot().previousClosed).toBe(2);
  expect(observer.hasProof()).toBe(false);
  observer.onHandshake(connect(observer), handshake);
  expect(observer.hasProof()).toBe(true);
});

test('requires a live initial handshake and rejects duplicate control boundaries', () => {
  const observer = createCaseRestartObserver(api);
  const initial = connect(observer);
  expect(() => observer.beginRestart()).toThrow('established notifications');
  observer.onHandshake(initial, handshake);
  initial.close();
  expect(() => observer.beginRestart()).toThrow('established notifications');
  const ready = established().observer;
  expect(() => ready.beginRestart()).toThrow('only once');
  ready.acknowledgeRestart();
  expect(() => ready.acknowledgeRestart()).toThrow('out of order');
});

test('the exact serialized factory works without module closures or performance', () => {
  const factory = vm.runInNewContext('(' + createCaseRestartObserver.toString() + ')', { URL });
  const observer = factory(api);
  const initial = socket();
  observer.observeSocket(initial);
  observer.onHandshake(initial, handshake);
  observer.beginRestart();
  close(observer, initial);
  const next = socket();
  observer.observeSocket(next);
  observer.onHandshake(next, handshake);
  observer.acknowledgeRestart();
  expect(observer.hasProof()).toBe(true);
});
