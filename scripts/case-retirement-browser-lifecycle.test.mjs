// @vitest-environment node

import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { runInNewContext } from 'node:vm';
import { describe, it } from 'vitest';
import { completeCaseUploadResponse, createCaseReadLifecycle, createCaseRetirementProofTrace, createCaseStartupReadTrace, isCaseBoundaryRead, observeCaseRetirementOperation, waitForCaseRestartResolution } from './case-retirement-browser-lifecycle.mjs';

describe('real retirement operation observation', () => {
  const key = 'private-owned-key';
  const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

  it('preserves the real receiver, arguments, promise and result in the serialized VM', async () => {
    const pending = deferred(); const signal = {}; const token = 'private-token';
    const service = { retireCaseDocumentUpload(...args) {
      assert.equal(this, service); assert.deepEqual(args, [7, key, signal, token]); return pending.promise;
    } };
    const original = service.retireCaseDocumentUpload;
    const create = runInNewContext('(' + observeCaseRetirementOperation.toString() + ')');
    const observer = create(service, 7, key);
    assert.equal(observer.snapshot().phase, 'idle');
    const returned = service.retireCaseDocumentUpload(7, key, signal, token);
    assert.equal(returned, pending.promise); assert.equal(observer.snapshot().phase, 'pending');
    const receipt = { uploadKey: key.toUpperCase(), status: 'retired', fileName: 'private-name' };
    pending.resolve(receipt); assert.equal(await returned, receipt); await flush();
    assert.equal(observer.snapshot().phase, 'fulfilled'); assert.equal(observer.snapshot().ownedTerminal, true);
    assert.equal(observer.snapshot().receiptStatus, 'retired'); assert.equal(observer.snapshot().attempt, 1);
    assert.doesNotMatch(JSON.stringify(observer.snapshot()), /private-|PRIVATE-/);
    const copy = observer.snapshot(); copy.phase = 'rejected'; assert.equal(observer.snapshot().phase, 'fulfilled');
    observer.restore(); assert.equal(service.retireCaseDocumentUpload, original);
  });

  for (const synchronous of [false, true]) it('preserves the exact original failure, synchronous=' + synchronous, async () => {
    const failure = new Error('private-original-error');
    const service = { retireCaseDocumentUpload() { if (synchronous) throw failure; return Promise.reject(failure); } };
    const observer = observeCaseRetirementOperation(service, 7, key);
    if (synchronous) assert.throws(() => service.retireCaseDocumentUpload(7, key), error => error === failure);
    else await assert.rejects(service.retireCaseDocumentUpload(7, key), error => error === failure);
    await flush(); assert.equal(observer.snapshot().phase, 'rejected');
    assert.equal(observer.snapshot().ownedTerminal, false); assert.doesNotMatch(JSON.stringify(observer.snapshot()), /private-/);
  });

  it('forwards foreign operations but never uses them as the owned result', async () => {
    let calls = 0; const receipt = { uploadKey: key, status: 'retired' };
    const service = { async retireCaseDocumentUpload() { calls++; return receipt; } };
    const observer = observeCaseRetirementOperation(service, 7, key);
    assert.equal(await service.retireCaseDocumentUpload(8, key), receipt);
    assert.equal(await service.retireCaseDocumentUpload(7, 'foreign-private-key'), receipt);
    await flush(); assert.equal(calls, 2); assert.equal(observer.snapshot().attempt, 0);
  });

  it('restores the actual class prototype method without leaving a new own property', () => {
    class Service { async retireCaseDocumentUpload() { return { uploadKey: key, status: 'retired' }; } }
    const service = new Service(); const original = service.retireCaseDocumentUpload;
    const observer = observeCaseRetirementOperation(service, 7, key);
    assert.equal(Object.hasOwn(service, 'retireCaseDocumentUpload'), true);
    assert.deepEqual(Object.keys(service), []);
    observer.restore();
    assert.equal(Object.hasOwn(service, 'retireCaseDocumentUpload'), false);
    assert.equal(service.retireCaseDocumentUpload, original);
  });

  for (const [label, receipt] of [['null', null], ['empty', {}], ['foreign', { uploadKey: 'foreign-private-key', status: 'retired' }],
    ['unknown', { uploadKey: key, status: 'unknown' }], ['unproved-upload', { uploadKey: key, status: 'uploaded' }]])
    it('does not acknowledge an invalid actual result: ' + label, async () => {
      const service = { async retireCaseDocumentUpload() { return receipt; } };
      const observer = observeCaseRetirementOperation(service, 7, key);
      assert.equal(await service.retireCaseDocumentUpload(7, key), receipt); await flush();
      assert.equal(observer.snapshot().phase, 'fulfilled'); assert.equal(observer.snapshot().ownedTerminal, false);
      assert.doesNotMatch(JSON.stringify(observer.snapshot()), /private-/);
    });

  it('requires complete owned publication proof and ignores late results of a prior attempt', async () => {
    const old = deferred(); const next = deferred(); let calls = 0;
    const service = { retireCaseDocumentUpload() { return ++calls === 1 ? old.promise : next.promise; } };
    const observer = observeCaseRetirementOperation(service, 7, key);
    const first = service.retireCaseDocumentUpload(7, key);
    const second = service.retireCaseDocumentUpload(7, key);
    next.resolve({ uploadKey: key, status: 'uploaded', documentId: 1, version: 2, sha256: 'a'.repeat(64), documentKey: 'private-document' });
    await second; await flush(); assert.equal(observer.snapshot().ownedTerminal, true);
    old.resolve({ uploadKey: key, status: 'unknown' }); await first; await flush();
    assert.equal(observer.snapshot().attempt, 2); assert.equal(observer.snapshot().ownedTerminal, true);
    const replacement = () => {}; service.retireCaseDocumentUpload = replacement;
    observer.restore(); assert.equal(service.retireCaseDocumentUpload, replacement);
  });
});

describe('bounded owned retirement proof diagnostics', () => {
  const api = 'http://127.0.0.1:41234';
  const key = 'private-expected-key';
  const response = (payload, { origin = api, path = '/api/v2/cases/7/documents/uploads',
    keys = [key], method = 'GET', status = 200 } = {}) => ({
    request: () => ({ url: () => origin + path + '?' + new URLSearchParams(keys.map(value => ['uploadKeys', value])), method: () => method }),
    status: () => status, json: () => payload,
  });
  const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

  it('records body completion separately, works in the serialized VM and retains no key or payload', async () => {
    const page = new EventEmitter(); const body = deferred();
    const factory = runInNewContext('(' + createCaseRetirementProofTrace.toString() + ')', { URL });
    const trace = factory(page, api, 7, key);
    page.emit('response', response(body.promise));
    assert.equal(trace.snapshot()[0].body, 'pending');
    assert.equal(trace.snapshot()[0].exactRetired, false);
    body.resolve([{ uploadKey: key.toUpperCase(), status: 'retired', fileName: 'private-filename', authorization: 'private-token' }]);
    await flush();
    assert.equal(trace.snapshot()[0].exactRetired, true);
    assert.equal(trace.snapshot()[0].count, 1);
    assert.equal(trace.snapshot()[0].body, 'array');
    assert.equal(typeof trace.snapshot()[0].bodyAtMs, 'number');
    assert.doesNotMatch(JSON.stringify(trace.snapshot()), /private-|PRIVATE-/);
    const copy = trace.snapshot(); copy[0].exactRetired = false;
    assert.equal(trace.snapshot()[0].exactRetired, true);
    trace.dispose();
    page.emit('response', response([]));
    assert.equal(trace.snapshot().length, 1);
  });

  for (const options of [{ origin: 'http://127.0.0.1:41235' }, { path: '/api/v2/cases/8/documents/uploads' },
    { path: '/api/v2/cases/7/documents/uploads/pending' }, { keys: ['foreign-private-key'] }, { keys: [key, key] }, { method: 'POST' }])
    it('ignores a response outside the exact owned read: ' + JSON.stringify(options), async () => {
      const page = new EventEmitter(); const trace = createCaseRetirementProofTrace(page, api, 7, key);
      page.emit('response', response([{ uploadKey: key, status: 'retired' }], options));
      await flush(); assert.equal(trace.snapshot().length, 0); trace.dispose();
    });

  for (const [label, payload] of [['null', null], ['object', {}], ['empty', []],
    ['foreign', [{ uploadKey: 'foreign-private-key', status: 'retired' }]],
    ['published', [{ uploadKey: key, status: 'uploaded' }]],
    ['duplicate', [{ uploadKey: key, status: 'retired' }, { uploadKey: key, status: 'retired' }]]])
    it('does not infer the exact retired receipt from an unrelated or ambiguous body: ' + label, async () => {
      const page = new EventEmitter(); const trace = createCaseRetirementProofTrace(page, api, 7, key);
      page.emit('response', response(payload)); await flush();
      assert.equal(trace.snapshot()[0].exactRetired, false); assert.doesNotMatch(JSON.stringify(trace.snapshot()), /private-/); trace.dispose();
    });

  it('bounds records and row counts, preserves body failure and rejects an error-status receipt', async () => {
    const page = new EventEmitter(); const trace = createCaseRetirementProofTrace(page, api, 7, key);
    page.emit('response', { ...response(null), json: () => { throw new Error('private-response-error'); } });
    page.emit('response', response([{ uploadKey: key, status: 'retired' }], { status: 503 }));
    page.emit('response', response(Array(100).fill({ uploadKey: key, status: 'retired' })));
    for (let index = 0; index < 70; index++) page.emit('response', response([]));
    await flush();
    assert.equal(trace.snapshot().length, 64); assert.equal(trace.snapshot()[0].body, 'unreadable');
    assert.equal(trace.snapshot()[1].exactRetired, false); assert.equal(trace.snapshot()[2].count, 65);
    assert.doesNotMatch(JSON.stringify(trace.snapshot()), /private-/); trace.dispose();
  });
});

describe('restart proof resolution', () => {
  for (const unresolvedResult of [true, false]) it(`waits for the late UI result before choosing unresolved=${unresolvedResult}`, async () => {
    const ready = deferred();
    const succeeded = {};
    const outcome = {};
    let inspected = false;
    const unresolved = {
      or: other => { assert.equal(other, succeeded); return outcome; },
      isVisible: async () => { inspected = true; return unresolvedResult; },
    };
    const wait = runInNewContext('(' + waitForCaseRestartResolution.toString() + ')');
    const pending = wait(unresolved, succeeded, async (locator, phase) => {
      assert.equal(locator, outcome);
      assert.equal(phase, 'restart-resolution');
      await ready.promise;
    });
    await Promise.resolve();
    assert.equal(inspected, false);
    ready.resolve();
    assert.equal(await pending, unresolvedResult);
    assert.equal(inspected, true);
  });

  it('preserves the original readiness failure without guessing a branch', async () => {
    const failure = new Error('original bounded readiness failure');
    const unresolved = { or: () => ({}), isVisible: () => { throw new Error('Premature result inspection'); } };
    await assert.rejects(waitForCaseRestartResolution(unresolved, {}, async () => { throw failure; }), error => error === failure);
  });
});

describe('upload response read boundary', () => {
  it('fetches once and retains the exact response until prior reads complete', async () => {
    const ready = deferred(); const entered = deferred(); const response = {}; const order = [];
    const route = {
      fetch: async () => { order.push('fetch'); return response; },
      fulfill: async options => { assert.equal(options.response, response); order.push('fulfill'); return 'forwarded'; },
    };
    const finish = runInNewContext('(' + completeCaseUploadResponse.toString() + ')');
    const pending = finish(route, { drain: async phase => { assert.equal(phase, 'document-upload'); order.push('drain'); entered.resolve(); await ready.promise; } });
    await entered.promise;
    assert.deepEqual(order, ['fetch', 'drain']);
    ready.resolve(); assert.equal(await pending, 'forwarded');
    assert.deepEqual(order, ['fetch', 'drain', 'fulfill']);
  });
  for (const stage of ['fetch', 'drain']) it(`preserves the original ${stage} failure without forwarding or retrying`, async () => {
    const failure = new Error('original boundary failure'); let fetches = 0;
    const route = {
      fetch: async () => { fetches++; if (stage === 'fetch') throw failure; return {}; },
      fulfill: () => { throw new Error('Must not publish an incomplete boundary'); },
    };
    await assert.rejects(completeCaseUploadResponse(route, { drain: async () => { throw failure; } }), error => error === failure);
    assert.equal(fetches, 1);
  });
});

describe('durable restart resolution', () => {
  it('uses the settled real operation when reconnect replaces its notice with a durable receipt', async () => {
    const locator = names => ({ names, or: other => locator([...names, ...other.names]),
      first: () => ({ names }), isVisible: async () => names.includes('retired') });
    const wait = runInNewContext('(' + waitForCaseRestartResolution.toString() + ')');
    const recovery = await wait(locator(['unresolved']), locator(['succeeded']), async () => {}, locator(['retired']),
      { attempt: 1, phase: 'fulfilled', ownedTerminal: true, receiptStatus: 'retired' });
    assert.equal(recovery, false, 'An actually acknowledged retirement is not an unresolved operation.');
  });

  for (const operation of [{ attempt: 0, phase: 'idle' }, { attempt: 1, phase: 'pending' },
    { attempt: 2, phase: 'fulfilled', ownedTerminal: true, receiptStatus: 'retired' },
    { attempt: 1, phase: 'fulfilled', ownedTerminal: false, receiptStatus: 'retired' },
    { attempt: 1, phase: 'fulfilled', ownedTerminal: 'yes', receiptStatus: 'retired' },
    { attempt: 1, phase: 'fulfilled', ownedTerminal: true, receiptStatus: 'unknown' }])
    it('does not infer an acknowledgement from an unsettled, foreign or unproved operation: ' + JSON.stringify(operation), async () => {
      await assert.rejects(waitForCaseRestartResolution({}, {}, async () => { throw new Error('UI cannot establish the real result'); }, {}, operation),
        /no settled terminal result/);
    });

  it('keeps rejected operations on the explicit recovery path even when the durable row is visible', async () => {
    const locator = names => ({ names, or: other => locator([...names, ...other.names]), first: () => ({ names }) });
    assert.equal(await waitForCaseRestartResolution(locator(['unresolved']), locator(['succeeded']), async outcome => {
      assert.deepEqual(outcome.names, ['unresolved', 'retired']);
    }, locator(['retired']), { attempt: 1, phase: 'rejected', ownedTerminal: false, receiptStatus: 'unknown' }), true);
  });

  it('does not accept a retired row as proof that a publication result is visible', async () => {
    const succeeded = {}; let observed;
    assert.equal(await waitForCaseRestartResolution({}, succeeded, async outcome => { observed = outcome; }, {},
      { attempt: 1, phase: 'fulfilled', ownedTerminal: true, receiptStatus: 'uploaded' }), false);
    assert.equal(observed, succeeded);
  });

  it('preserves the bounded UI failure after the real operation settles', async () => {
    const failure = new Error('original UI deadline');
    const succeeded = { or: () => ({ first: () => ({}) }) };
    await assert.rejects(waitForCaseRestartResolution({}, succeeded, async () => { throw failure; }, {},
      { attempt: 1, phase: 'fulfilled', ownedTerminal: true, receiptStatus: 'retired' }), error => error === failure);
  });

  for (const visible of [['retired'], ['unresolved'], ['retired', 'unresolved'], ['succeeded'], ['succeeded', 'retired']])
    it(`chooses explicit recovery for retained state ${visible.join('+')}`, async () => {
      const locator = names => ({ names, or: other => locator([...names, ...other.names]),
        first: () => ({ names, firstOnly: true }), isVisible: async () => names.some(name => visible.includes(name)) });
      const wait = runInNewContext('(' + waitForCaseRestartResolution.toString() + ')');
      assert.equal(await wait(locator(['unresolved']), locator(['succeeded']), async (outcome, phase) => {
        assert.equal(outcome.firstOnly, true);
        assert.deepEqual(outcome.names.sort(), ['retired', 'succeeded', 'unresolved']);
        assert.equal(phase, 'restart-resolution');
      }, locator(['retired'])), !visible.includes('succeeded'));
    });
  it('never interprets a failed durable readiness check as a successful release', async () => {
    const failure = new Error('no proven UI outcome');
    const unresolved = { or: () => ({ or: () => ({ first: () => ({}) }), isVisible: () => { throw new Error('Premature branch'); } }) };
    await assert.rejects(waitForCaseRestartResolution(unresolved, {}, async () => { throw failure; }, {}), error => error === failure);
  });
});

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

function fixture(timeoutMs = 20000) {
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
  const lifecycle = create(page, request => predicate(request, 'http://127.0.0.1:1234', 7), timeoutMs);
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

  it.each([5000, 20000])('keeps the original %i ms deadline when a read never finishes', async timeoutMs => {
    const { page, lifecycle, request, timers } = fixture(timeoutMs);
    page.emit('request', request().value);
    const drain = lifecycle.drain('server-restart');
    assert.equal(timers.size, 1);
    [...timers][0]();
    await assert.rejects(drain, new RegExp('within ' + timeoutMs + 'ms'));
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

  it('distinguishes an unresponsive render frame from unfinished response bodies without exposing request details', async () => {
    const { page, lifecycle, timers } = fixture(5000);
    const entered = deferred();
    const frame = deferred();
    page.evaluate = () => { entered.resolve(); return frame.promise; };
    const drain = lifecycle.drain('browser-close');
    await entered.promise;
    [...timers][0]();
    await assert.rejects(drain, /within 5000ms \(pending=0, frame=true\)/);
    assert.equal(timers.size, 0);
    frame.resolve();
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
