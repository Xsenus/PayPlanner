import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

afterEach(() => vi.unstubAllGlobals());
it('client transport: all and paged reads preserve filters and the owned signal without caching', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json([])).mockResolvedValueOnce(Response.json({ items: [], total: 0 }));
    vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getClients({ signal: controller.signal });
    await apiService.getClientsV2({ search: 'Иван & Пётр', isActive: false, sortBy: 'createdAt', sortDir: 'desc', page: 3, pageSize: 20, clientStatusId: '9', note: 'a=b&c' }, { signal: controller.signal });
    const parsed = new URL(String(fetch.mock.calls[1][0]), 'http://localhost');
    expect(parsed.pathname).toMatch(/\/v2\/clients$/);
    expect(Object.fromEntries(parsed.searchParams)).toEqual({ search: 'Иван & Пётр', isActive: 'false', sortBy: 'createdAt', sortDir: 'desc', page: '3', pageSize: '20', clientStatusId: '9', note: 'a=b&c' });
    for (const [, options] of fetch.mock.calls) { expect(options?.signal).toBe(controller.signal); expect(options?.cache).toBe('no-store'); }
    expect(fetch).toHaveBeenCalledTimes(2);
  } finally { session.restore(); }
});
it('client transport: statistics keeps legacy overloads and sends owned case and role without caching', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json({ clientId: 7 })); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getClientStats(7); await apiService.getClientStats(7, { caseId: 8 });
    await apiService.getClientStats(7, { caseId: 9, clientRole: 'expenseClient' }, { signal: controller.signal });
    const parsed = new URL(String(fetch.mock.calls[2][0]), 'http://localhost');
    expect(parsed.pathname).toMatch(/\/clients\/7\/stats$/); expect(Object.fromEntries(parsed.searchParams)).toEqual({ caseId: '9', clientRole: 'expenseClient' });
    expect(fetch.mock.calls[2][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls.every(([, options]) => options?.cache === 'no-store')).toBe(true); expect(fetch).toHaveBeenCalledTimes(3);
  } finally { session.restore(); }
});
