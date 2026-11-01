import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

afterEach(() => vi.unstubAllGlobals());
it('dictionary transport: generic groups and statuses retain legacy calls and owned uncached reads', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getDict('countries'); await apiService.getDict('countries', { signal: controller.signal });
    await apiService.getClientStatuses(); await apiService.getClientStatuses({ signal: controller.signal });
    expect(fetch.mock.calls.map(([url]) => new URL(String(url), 'http://localhost').pathname.split('/').at(-1))).toEqual(['countries', 'countries', 'client-statuses', 'client-statuses']);
    expect(fetch.mock.calls[1][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls[3][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls.every(([, options]) => options?.cache === 'no-store')).toBe(true);
  } finally { session.restore(); }
});
it('dictionary transport: income overloads preserve type and active filters with an optional signal', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getIncomeTypes(); await apiService.getIncomeTypes('Expense', false); await apiService.getIncomeTypes('Income', true, { signal: controller.signal });
    const urls = fetch.mock.calls.map(([url]) => new URL(String(url), 'http://localhost'));
    expect(urls[0].search).toBe(''); expect(Object.fromEntries(urls[1].searchParams)).toEqual({ paymentType: 'Expense', isActive: 'false' }); expect(Object.fromEntries(urls[2].searchParams)).toEqual({ paymentType: 'Income', isActive: 'true' });
    expect(fetch.mock.calls[2][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls.every(([, options]) => options?.cache === 'no-store')).toBe(true); expect(fetch).toHaveBeenCalledTimes(3);
  } finally { session.restore(); }
});
