import { describe, expect, it } from 'vitest';

import {
  AUTHENTICATED_ROUTES,
  PUBLIC_ROUTES,
  formatPageFailure,
  isIgnoredConsoleMessage,
  parseArgs,
  readBoolean,
  readPositiveInt,
  resolveRoutes,
  resolveViewportNames,
  trimTrailingSlash,
} from './run-visual-smoke.mjs';

describe('run-visual-smoke configuration', () => {
  it('parses cli arguments in dashed and equals forms', () => {
    expect(parseArgs(['--frontend-url=https://crm.example', '--timeout-ms', '30000'])).toEqual({
      frontendUrl: 'https://crm.example',
      timeoutMs: '30000',
    });
  });

  it('resolves public and authenticated default routes', () => {
    expect(resolveRoutes('', false)).toEqual(PUBLIC_ROUTES);
    expect(resolveRoutes('', true)).toEqual(AUTHENTICATED_ROUTES);
    expect(AUTHENTICATED_ROUTES[0]).toEqual({ name: 'calendar', path: '/?ppTab=calendar' });
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
