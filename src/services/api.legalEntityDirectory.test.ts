import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

afterEach(() => vi.unstubAllGlobals());
it('reads the same legal directory query without HTTP caching and with its owned signal', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getLegalEntities({ search: 'ООО & компания', counterpartyRole: 'supplier' }, { signal: controller.signal });
    const [url, options] = fetch.mock.calls[0]; const parsed = new URL(String(url), 'http://localhost');
    expect(parsed.pathname).toMatch(/\/legal-entities$/); expect(parsed.searchParams.get('search')).toBe('ООО & компания'); expect(parsed.searchParams.get('counterpartyRole')).toBe('supplier');
    expect(options?.signal).toBe(controller.signal); expect(options?.cache).toBe('no-store'); expect(fetch).toHaveBeenCalledOnce();
  } finally { session.restore(); }
});
