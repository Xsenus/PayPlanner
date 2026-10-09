import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';

import {
  dismissNavigationPrompts,
  observeFetchCancellations,
  withFreshRoutePage,
  safeResourceUrl,
  withRouteDiagnostics,
  APP_THEME_KEYS,
  AUTHENTICATED_ROUTES,
  FULL_APP_ROUTES,
  PUBLIC_ROUTES,
  formatPageFailure,
  isIgnoredConsoleMessage,
  parseArgs,
  readBoolean,
  readPositiveInt,
  resolveRoutes,
  resolveThemeNames,
  resolveViewportNames,
  trimTrailingSlash,
} from './run-visual-smoke.mjs';

describe('visual smoke route ownership and navigation prompts', () => {
  it('closes the covering release dialog before the daily reminder and waits for each to disappear', async () => {
    const calls = [];
    const page = { getByRole(role, options) {
      expect(role).toBe('dialog');
      expect(options.exact).toBe(true);
      return {
        count: async () => 1,
        getByRole(buttonRole, buttonOptions) {
          expect(buttonRole).toBe('button');
          expect(buttonOptions.exact).toBe(true);
          return { click: async args => { expect(args.timeout).toBe(1000); calls.push([options.name, buttonOptions.name]); } };
        },
        waitFor: async args => { expect(args).toEqual({ state: 'hidden', timeout: 1000 }); calls.push([options.name, 'hidden']); },
      };
    } };
    expect(await dismissNavigationPrompts(page)).toBe(2);
    expect(calls).toEqual([['Что нового', 'Понятно'], ['Что нового', 'hidden'], ['Внимание!', 'Закрыть'], ['Внимание!', 'hidden']]);
  });

  it('does not click other business dialogs and propagates failure to dismiss a known prompt', async () => {
    const queried = [];
    const page = { getByRole: (_role, options) => {
      queried.push(options.name);
      return { count: async () => 0, getByRole: () => { throw new Error('Unexpected click'); } };
    } };
    expect(await dismissNavigationPrompts(page)).toBe(0);
    expect(queried).toEqual(['Что нового', 'Внимание!']);
    const failed = new Error('Prompt blocked');
    page.getByRole = () => ({ count: async () => 1, getByRole: () => ({ click: async () => { throw failed; } }) });
    await expect(dismissNavigationPrompts(page)).rejects.toBe(failed);
  });

  it('gives each route its own page and closes it before opening the next', async () => {
    const calls = [];
    const context = { newPage: async () => {
      const id = calls.filter(x => x[0] === 'open').length;
      calls.push(['open', id]);
      return { addInitScript: async observer => { expect(observer).toBe(observeFetchCancellations); }, setViewportSize: async size => calls.push(['viewport', id, size]), close: async () => calls.push(['close', id]) };
    } };
    const viewport = { width: 390, height: 844 };
    for (let i = 0; i < 3; i++) {
      expect(await withFreshRoutePage(context, viewport, async () => { calls.push(['inspect', i]); return i; })).toBe(i);
    }
    expect(calls).toEqual([0, 1, 2].flatMap(i => [['open', i], ['viewport', i, viewport], ['inspect', i], ['close', i]]));
  });

  it('closes the owned page after viewport or inspection failure and preserves the primary failure', async () => {
    const primary = new Error('Original check failure');
    const close = vi.fn(async () => { throw new Error('Close failure'); });
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const context = { newPage: async () => ({ addInitScript: async () => {}, setViewportSize: async () => { throw primary; }, close }) };
      await expect(withFreshRoutePage(context, {}, () => { throw new Error('Must not inspect'); })).rejects.toBe(primary);
      expect(close).toHaveBeenCalledTimes(1);
      const page = { addInitScript: async () => {}, setViewportSize: async () => {}, close: vi.fn(async () => {}) };
      await expect(withFreshRoutePage({ newPage: async () => page }, {}, async () => { throw primary; })).rejects.toBe(primary);
      expect(page.close).toHaveBeenCalledTimes(1);
      await expect(withFreshRoutePage({ newPage: async () => ({ ...page, close }) }, {}, async () => 'ok')).rejects.toThrow('Close failure');
    } finally {
      log.mockRestore();
    }
  });
});

describe('run-visual-smoke configuration', () => {
  it('parses cli arguments in dashed and equals forms', () => {
    expect(parseArgs(['--frontend-url=https://crm.example?mode=full', '--timeout-ms', '30000', '--selection-preview=true'])).toEqual({
      frontendUrl: 'https://crm.example?mode=full',
      timeoutMs: '30000',
      selectionPreview: 'true',
    });
  });

  it('resolves public and authenticated default routes', () => {
    expect(resolveRoutes('', false)).toEqual(PUBLIC_ROUTES);
    expect(resolveRoutes('', true)).toEqual(AUTHENTICATED_ROUTES);
    expect(AUTHENTICATED_ROUTES[0]).toEqual({ name: 'calendar', path: '/?ppTab=calendar' });
    expect(resolveRoutes('', true, true)).toEqual(FULL_APP_ROUTES);
    expect(FULL_APP_ROUTES).toHaveLength(46);
  });

  it('resolves one, several and all visual themes', () => {
    expect(resolveThemeNames('')).toEqual([null]);
    expect(resolveThemeNames('warm,midnight,warm')).toEqual(['warm', 'midnight']);
    expect(resolveThemeNames('all')).toEqual(APP_THEME_KEYS);
    expect(() => resolveThemeNames('all,warm')).toThrow(/cannot be combined/);
    expect(() => resolveThemeNames('unknown')).toThrow(/Unknown theme/);
  });

  it('normalizes custom routes', () => {
    expect(resolveRoutes('tasks,/payments?ppTab=paymentsIncome,https://crm.example/health', true)).toEqual([
      { name: 'tasks', path: '/tasks' },
      { name: 'payments-pptab-paymentsincome', path: '/payments?ppTab=paymentsIncome' },
      { name: 'crm-example-health', path: 'https://crm.example/health' },
    ]);
  });

  it('validates viewport names', () => {
    expect(resolveViewportNames('mobile,desktop')).toEqual(['mobile', 'desktop']);
    expect(() => resolveViewportNames('watch')).toThrow(/Unknown viewport/);
  });

  it('reads booleans and positive timeouts predictably', () => {
    expect(readBoolean('true')).toBe(true);
    expect(readBoolean('0')).toBe(false);
    expect(readPositiveInt('15', 20)).toBe(15);
    expect(readPositiveInt('', 20)).toBe(20);
    expect(() => readPositiveInt('-1', 20)).toThrow(/positive integer/);
  });

  it('keeps known noisy browser messages out of console error failures', () => {
    expect(isIgnoredConsoleMessage('ResizeObserver loop completed with undelivered notifications.')).toBe(true);
    expect(isIgnoredConsoleMessage('Failed to load resource: the server responded with a status of 404 (favicon.ico)')).toBe(true);
    expect(isIgnoredConsoleMessage('Uncaught TypeError: broken')).toBe(false);
  });

  it('formats failures with viewport and route context', () => {
    expect(formatPageFailure('tasks', 'mobile', 'horizontal overflow')).toBe('[mobile] tasks: horizontal overflow');
  });

  it('trims trailing slashes from base URLs', () => {
    expect(trimTrailingSlash('https://crm.example///')).toBe('https://crm.example');
  });
});

describe('visual smoke route diagnostics', () => {
  const events = ['console', 'pageerror', 'request', 'response', 'requestfailed'];
  const request = (url, reason = 'net::ERR_CONNECTION_RESET', startedAt = 1) => ({
    url: () => url, method: () => 'GET', failure: () => ({ errorText: reason }),
    timing: () => ({ startTime: startedAt }),
  });

  it('requires an actual 204 response for Chromium no-content aborts and retains other failures', async () => {
    const page = new EventEmitter();
    await withRouteDiagnostics(page, async diagnostics => {
      const noContent = request('https://api.example/activity', 'net::ERR_ABORTED');
      page.emit('response', { status: () => 204, request: () => noContent });
      page.emit('requestfailed', noContent);
      const body = request('https://api.example/body', 'net::ERR_ABORTED');
      page.emit('response', { status: () => 200, request: () => body });
      page.emit('requestfailed', body);
      const reset = request('https://api.example/reset', 'net::ERR_CONNECTION_RESET');
      page.emit('response', { status: () => 204, request: () => reset });
      page.emit('requestfailed', reset);
      page.emit('requestfailed', request('https://api.example/missing-response', 'net::ERR_ABORTED'));
      expect(diagnostics.expectedNoContentCompletions).toEqual(['GET https://api.example/activity']);
      expect(diagnostics.networkErrors).toHaveLength(3);
    });
  });

  it('requires one exact browser pre-aborted signal per undispatched cancellation', async () => {
    const page = new EventEmitter();
    const url = 'https://api.example/summary?filter=one';
    page.evaluate = async () => [{ url, method: 'GET' }];
    await withRouteDiagnostics(page, async diagnostics => {
      for (const value of [url, url, 'https://api.example/summary?filter=two']) {
        const item = request(value, 'net::ERR_ABORTED', 0);
        page.emit('request', item); page.emit('requestfailed', item);
      }
      const dispatched = request(url, 'net::ERR_ABORTED', 100);
      page.emit('request', dispatched); page.emit('requestfailed', dispatched);
      await diagnostics.confirmPreDispatchAborts();
      expect(diagnostics.expectedPreDispatchAborts).toEqual(['GET https://api.example/summary']);
      expect(diagnostics.networkErrors).toHaveLength(3);
      expect(JSON.stringify(diagnostics.networkErrors)).not.toContain('filter=');
      await diagnostics.confirmPreDispatchAborts();
      expect(diagnostics.networkErrors).toHaveLength(3);
    });
  });

  it('does not treat missing browser proof or unobserved/unknown timing as successful cancellation', async () => {
    const page = new EventEmitter();
    page.evaluate = async () => [];
    await withRouteDiagnostics(page, async diagnostics => {
      for (const timing of [0, -1]) {
        const item = request('https://api.example/no-proof', 'net::ERR_ABORTED', timing);
        page.emit('request', item); page.emit('requestfailed', item);
      }
      page.emit('requestfailed', request('https://api.example/not-observed', 'net::ERR_ABORTED', 0));
      await diagnostics.confirmPreDispatchAborts();
      expect(diagnostics.networkErrors).toHaveLength(3);
    });
  });

  it('observes pre-cancelled fetches without changing native arguments or return values and bounds metadata', async () => {
    const promise = Promise.resolve();
    const native = vi.fn(() => promise);
    vi.stubGlobal('fetch', native);
    try {
      observeFetchCancellations();
      const controller = new AbortController();
      const options = { method: 'POST', signal: controller.signal };
      expect(globalThis.fetch('https://api.example/live', options)).toBe(promise);
      expect(globalThis.__payplannerVisualCancelledFetches).toEqual([]);
      controller.abort();
      for (let index = 0; index < 140; index++)
        expect(globalThis.fetch(`https://api.example/pre-aborted?case=${index}#fragment`, options)).toBe(promise);
      expect(native.mock.calls[0][1]).toBe(options);
      expect(globalThis.__payplannerVisualCancelledFetches).toHaveLength(128);
      expect(globalThis.__payplannerVisualCancelledFetches.at(-1)).toEqual({ url: 'https://api.example/pre-aborted?case=139', method: 'POST' });
    } finally {
      vi.unstubAllGlobals();
      delete globalThis.__payplannerVisualCancelledFetches;
    }
  });

  it('records cancellation during pending fetch and removes its observer after native settlement', async () => {
    let settle;
    const pending = new Promise(resolve => { settle = resolve; });
    vi.stubGlobal('fetch', () => pending);
    try {
      observeFetchCancellations();
      const cancelled = new AbortController();
      const completed = new AbortController();
      expect(globalThis.fetch('https://api.example/cancelled', { signal: cancelled.signal })).toBe(pending);
      expect(globalThis.fetch('https://api.example/completed', { signal: completed.signal })).toBe(pending);
      cancelled.abort();
      expect(globalThis.__payplannerVisualCancelledFetches).toEqual([{ url: 'https://api.example/cancelled', method: 'GET' }]);
      settle();
      await pending;
      completed.abort();
      expect(globalThis.__payplannerVisualCancelledFetches).toHaveLength(1);
    } finally {
      vi.unstubAllGlobals();
      delete globalThis.__payplannerVisualCancelledFetches;
    }
  });

  it('releases route listeners across the complete 46-route loop and on failure', async () => {
    const page = new EventEmitter();
    const retained = () => {};
    page.on('response', retained);
    for (let index = 0; index < 46; index++) {
      await withRouteDiagnostics(page, async diagnostics => {
        expect(page.listenerCount('console')).toBe(1);
        expect(page.listenerCount('response')).toBe(2);
        page.emit('pageerror', new Error(`route-${index}`));
        expect(diagnostics.pageErrors).toEqual([`route-${index}`]);
      });
      for (const event of events) expect(page.listenerCount(event)).toBe(event === 'response' ? 1 : 0);
    }
    await expect(withRouteDiagnostics(page, async () => { throw new Error('navigation failed'); })).rejects.toThrow('navigation failed');
    for (const event of events) expect(page.listenerCount(event)).toBe(event === 'response' ? 1 : 0);
  });

  it('fails silent HTTP and transport errors without retaining query strings or credentials', async () => {
    const page = new EventEmitter();
    await withRouteDiagnostics(page, async diagnostics => {
      const failed = request('https://name:password@api.example/api/tasks/123?access_token=secret#private');
      page.emit('request', failed);
      for (const status of [200, 304, 401, 404, 503])
        page.emit('response', { status: () => status, request: () => failed });
      page.emit('requestfailed', failed);
      expect(diagnostics.networkErrors).toEqual([
        'HTTP 401: GET https://api.example/api/tasks/:id',
        'HTTP 404: GET https://api.example/api/tasks/:id',
        'HTTP 503: GET https://api.example/api/tasks/:id',
        'requestfailed: GET https://api.example/api/tasks/:id (net::ERR_CONNECTION_RESET)',
      ]);
      expect(JSON.stringify(diagnostics.networkErrors)).not.toMatch(/secret|password|private|access_token/);
    });
    expect(safeResourceUrl('not a URL?access_token=secret')).toBe('<invalid-resource-url>');
  });

  it('retains network evidence when navigation itself throws and rethrows the original error', async () => {
    const page = new EventEmitter();
    const original = new Error('navigation timeout');
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      await expect(withRouteDiagnostics(page, async () => {
        page.emit('response', { status: () => 503, request: () => request('https://api.example/api/check?token=secret') });
        throw original;
      })).rejects.toBe(original);
      expect(log).toHaveBeenCalledTimes(1);
      expect(log.mock.calls[0][0]).toContain('HTTP 503: GET https://api.example/api/check');
      expect(log.mock.calls[0][0]).not.toContain('secret');
      for (const event of events) expect(page.listenerCount(event)).toBe(0);
    } finally {
      log.mockRestore();
    }
  });

  it('classifies only proven previous-page navigation aborts and fails every other abort', async () => {
    const page = new EventEmitter();
    await withRouteDiagnostics(page, async diagnostics => {
      diagnostics.beginNavigation();
      const previous = request('https://api.example/api/previous', 'net::ERR_ABORTED');
      page.emit('requestfailed', previous);
      const current = request('https://api.example/api/current', 'net::ERR_ABORTED');
      page.emit('request', current);
      page.emit('requestfailed', current);
      page.emit('requestfailed', request('https://api.example/api/unknown-timing', 'net::ERR_ABORTED', -1));
      diagnostics.endNavigation();
      page.emit('requestfailed', previous);
      page.emit('requestfailed', request('https://api.example/api/unknown', 'Unexpected failure: ?token=secret'));
      expect(diagnostics.expectedNavigationAborts).toEqual(['GET https://api.example/api/previous']);
      expect(diagnostics.networkErrors).toHaveLength(4);
      expect(diagnostics.networkErrors.at(-1)).toContain('unknown-transport-error');
      expect(JSON.stringify(diagnostics.networkErrors)).not.toContain('secret');
    });
  });
});
