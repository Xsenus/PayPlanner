import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';

import {
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
