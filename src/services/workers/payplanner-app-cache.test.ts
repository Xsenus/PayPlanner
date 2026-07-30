import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

type WorkerEventListener = (event: unknown) => void;

function loadWorker(options: { cachedIndex?: unknown } = {}) {
  const listeners = new Map<string, WorkerEventListener>();
  const addAll = vi.fn().mockResolvedValue(undefined);
  const put = vi.fn().mockResolvedValue(undefined);
  const cache = { addAll, put };
  const caches = {
    open: vi.fn().mockResolvedValue(cache),
    match: vi.fn().mockImplementation((request: unknown) => (
      request === '/index.html' ? options.cachedIndex : undefined
    )),
    keys: vi.fn().mockResolvedValue([
      'payplanner-app-shell-v0',
      'payplanner-app-shell-v1',
      'unrelated-cache',
    ]),
    delete: vi.fn().mockResolvedValue(true),
  };
  const clients = {
    claim: vi.fn().mockResolvedValue(undefined),
  };
  const self = {
    addEventListener: vi.fn((eventName: string, listener: WorkerEventListener) => {
      listeners.set(eventName, listener);
    }),
    location: { origin: 'https://crm.example.com' },
    clients,
  };
  const fetch = vi.fn();
  const script = readFileSync(resolve(process.cwd(), 'public/payplanner-app-cache.js'), 'utf8');

  runInNewContext(script, {
    self,
    caches,
    fetch,
    URL,
    Response,
  });

  return {
    listeners,
    caches,
    cache,
    clients,
    fetch,
  };
}

async function dispatchLifecycle(listener: WorkerEventListener) {
  const promises: Promise<unknown>[] = [];
  listener({
    waitUntil: (promise: Promise<unknown>) => promises.push(promise),
  });
  await Promise.all(promises);
}

function dispatchFetch(listener: WorkerEventListener, request: Record<string, unknown>) {
  let responsePromise: Promise<unknown> | undefined;
  listener({
    request,
    respondWith: (promise: Promise<unknown>) => {
      responsePromise = promise;
    },
  });
  return responsePromise;
}

describe('payplanner-app-cache', () => {
  it('preloads only the safe shell and removes only older PayPlanner cache versions', async () => {
    const { listeners, caches, cache, clients } = loadWorker();

    await dispatchLifecycle(listeners.get('install')!);
    await dispatchLifecycle(listeners.get('activate')!);

    expect(cache.addAll).toHaveBeenCalledWith([
      '/',
      '/index.html',
      '/manifest.webmanifest',
      '/logo.ico',
      '/logo.png',
    ]);
    expect(caches.delete).toHaveBeenCalledTimes(1);
    expect(caches.delete).toHaveBeenCalledWith('payplanner-app-shell-v0');
    expect(clients.claim).toHaveBeenCalledTimes(1);
  });

  it('never intercepts API, non-GET or cross-origin requests', () => {
    const { listeners } = loadWorker();
    const fetchListener = listeners.get('fetch')!;

    expect(dispatchFetch(fetchListener, {
      method: 'GET',
      mode: 'cors',
      url: 'https://crm.example.com/api/calendar',
    })).toBeUndefined();
    expect(dispatchFetch(fetchListener, {
      method: 'POST',
      mode: 'same-origin',
      url: 'https://crm.example.com/assets/app.js',
    })).toBeUndefined();
    expect(dispatchFetch(fetchListener, {
      method: 'GET',
      mode: 'cors',
      url: 'https://cdn.example.com/assets/app.js',
    })).toBeUndefined();
  });

  it('serves a cached app shell when navigation fails offline', async () => {
    const offlineShell = { source: 'cache' };
    const { listeners, fetch } = loadWorker({ cachedIndex: offlineShell });
    fetch.mockRejectedValue(new Error('offline'));

    const response = dispatchFetch(listeners.get('fetch')!, {
      method: 'GET',
      mode: 'navigate',
      url: 'https://crm.example.com/calendar',
    });

    await expect(response).resolves.toBe(offlineShell);
  });
});
