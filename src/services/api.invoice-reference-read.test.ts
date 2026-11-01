import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';

afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });
it('invoice reference read contract: preserves its route and forwards cancellation without cached financial defaults', async () => {
  const fetch = vi.fn().mockImplementation(async () => Response.json({ invoiceUnits: [], services: [], serviceCategories: [] })); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController(); await apiService.getInvoiceReferenceDictionaries({ signal: controller.signal });
  expect(String(fetch.mock.calls[0][0])).toMatch(/\/invoices\/reference-dictionaries$/);
  expect(fetch.mock.calls[0][1]).toMatchObject({ signal: controller.signal, cache: 'no-store' });
  await apiService.getInvoiceReferenceDictionaries(); expect(fetch.mock.calls[1][1].signal).toBeUndefined(); expect(fetch.mock.calls[1][1].cache).toBe('no-store');
});
