import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { bindClientInvoiceModalApi } from '../utils/clientInvoiceModalApi';

afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });
it('client invoice transport: current invoice GET preserves its ID and bypasses cached financial data with cancellation', async () => {
  const fetch = vi.fn().mockImplementation(async () => Response.json({ id: 91 })); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController(); await apiService.getInvoice(91, { signal: controller.signal }); await apiService.getInvoice(93);
  expect(String(fetch.mock.calls[0][0])).toMatch(/\/invoices\/91$/); expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  expect(String(fetch.mock.calls[1][0])).toMatch(/\/invoices\/93$/); expect(fetch.mock.calls[1][1].signal).toBeUndefined(); expect(fetch.mock.calls[1][1].cache).toBe('no-store');
});
it('client invoice transport: bound API keeps the same signal for every page and the narrower client fallback', async () => {
  const fetch = vi.fn().mockImplementation(async (url: string) => {
    const u = new URL(url, 'http://localhost');
    if (u.pathname.endsWith('/v2/payments')) return Response.json({ items: [{ id: u.searchParams.get('page') === '2' ? 302 : 301 }], total: 2, page: Number(u.searchParams.get('page')), pageSize: 1 });
    return Response.json([]);
  }); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController(); const bound = bindClientInvoiceModalApi(apiService); const read = { signal: controller.signal, isCurrent: () => true, canReadPayments: () => true };
  expect((await bound.fetchPayments({ invoiceId: 91, page: 1, pageSize: 1 }, read)).map(row => row.id)).toEqual([301, 302]);
  await bound.fetchPayments({ clientId: 7, from: '1900-01-01', to: '2100-12-31' }, read);
  expect(fetch).toHaveBeenCalledTimes(3); for (const call of fetch.mock.calls) expect(call[1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  const second = new URL(fetch.mock.calls[1][0]); expect(second.searchParams.get('invoiceId')).toBe('91'); expect(second.searchParams.get('page')).toBe('2');
  const fallback = new URL(fetch.mock.calls[2][0]); expect(fallback.pathname).toMatch(/\/payments$/); expect(fallback.searchParams.get('clientId')).toBe('7');
});
