import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { installTaskNotificationRefreshObserver } from './task-notification-refresh-observer.mjs';

const apiOrigin = 'http://127.0.0.1:5080';
const countUrl = `${apiOrigin}/api/notifications/inbox/unread-count`;
function browser(fetchImpl = async () => new Response('1')) {
  const listeners = new Map();
  const calls = [];
  const window = {
    location: { href: 'http://127.0.0.1:5173/' },
    addEventListener: (name, listener) => listeners.set(name, listener),
    fetch(...args) {
      assert.equal(this, window);
      calls.push(args);
      return fetchImpl(...args);
    },
  };
  vm.runInNewContext(`(${installTaskNotificationRefreshObserver.toString()})(apiOrigin)`, {
    window, apiOrigin, URL,
    Date: { now() { throw new Error('Clock comparisons must not decide request completion.'); } },
  });
  return {
    window, calls,
    notify: () => listeners.get('payplanner:user-notifications-changed')(),
    refreshStarted: () => window.__taskUploadUnreadCountStartedRevision >= window.__taskUploadNotificationRevision,
    refreshed: () => window.__taskUploadUnreadCountRefreshRevision >= window.__taskUploadNotificationRevision,
  };
}

test('observes a real successful read after the event and preserves fetch arguments and response', async () => {
  const response = new Response('7');
  const b = browser(async () => response);
  b.notify();
  assert.equal(b.refreshStarted(), false);
  assert.equal(b.refreshed(), false);
  const options = { headers: { Authorization: 'synthetic' }, cache: 'no-store' };
  assert.equal(await b.window.fetch(countUrl, options), response);
  assert.deepEqual(b.calls, [[countUrl, options]]);
  assert.equal(await response.text(), '7');
  assert.equal(b.refreshStarted(), true);
  assert.equal(b.refreshed(), true);
  b.notify();
  assert.equal(b.refreshStarted(), false);
  assert.equal(b.refreshed(), false);
});

test('a response to a request started before the event cannot satisfy the latest refresh', async () => {
  let complete;
  const b = browser(() => new Promise(resolve => { complete = resolve; }));
  const pending = b.window.fetch(countUrl);
  b.notify();
  complete(new Response('1'));
  await pending;
  assert.equal(b.refreshStarted(), false);
  assert.equal(b.refreshed(), false);
});

test('does not mark a pending or failed body as successfully refreshed', async () => {
  let completeBody;
  let bodyStarted;
  const started = new Promise(resolve => { bodyStarted = resolve; });
  const response = {
    status: 200,
    clone: () => ({ arrayBuffer: () => new Promise((resolve, reject) => {
      completeBody = { resolve, reject };
      bodyStarted();
    }) }),
  };
  const b = browser(async () => response);
  b.notify();
  const pending = b.window.fetch(countUrl);
  await started;
  assert.equal(b.refreshStarted(), true);
  assert.equal(b.refreshed(), false);
  completeBody.reject(new Error('synthetic connection interrupted'));
  assert.equal(await pending, response);
  assert.equal(b.refreshed(), false);
});

test('ignores non-success, other endpoints, foreign origins and non-GET requests', async () => {
  const b = browser(async () => new Response('1'));
  b.notify();
  for (const [url, options] of [
    [`${apiOrigin}/api/notifications/inbox`, undefined],
    ['http://127.0.0.1:5081/api/notifications/inbox/unread-count', undefined],
    [countUrl, { method: 'POST' }],
  ]) await b.window.fetch(url, options);
  assert.equal(b.refreshStarted(), false);
  assert.equal(b.refreshed(), false);
  const denied = browser(async () => new Response('denied', { status: 403 }));
  denied.notify();
  await denied.window.fetch(countUrl);
  assert.equal(denied.refreshed(), false);
});

test('preserves network rejection and handles Request objects without sending another request', async () => {
  const failure = new Error('synthetic network failure');
  const failed = browser(async () => { throw failure; });
  failed.notify();
  await assert.rejects(failed.window.fetch(countUrl), error => error === failure);
  assert.equal(failed.refreshed(), false);
  const b = browser();
  b.notify();
  await b.window.fetch(new Request(countUrl));
  assert.equal(b.refreshed(), true);
  assert.equal(b.calls.length, 1);
});
