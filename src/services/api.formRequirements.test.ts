import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

afterEach(() => vi.unstubAllGlobals());
it('requirements transport: no-argument settings and scoped form reads preserve query with owned uncached HTTP', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getFormRequirements(); await apiService.getFormRequirements('clients & legal', { signal: controller.signal });
    const first = new URL(String(fetch.mock.calls[0][0]), 'http://localhost'); const scoped = new URL(String(fetch.mock.calls[1][0]), 'http://localhost');
    expect(first.pathname).toMatch(/\/form-requirements$/); expect(first.search).toBe(''); expect(Object.fromEntries(scoped.searchParams)).toEqual({ section: 'clients & legal' });
    expect(fetch.mock.calls[1][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls.every(([, options]) => options?.cache === 'no-store')).toBe(true); expect(fetch).toHaveBeenCalledTimes(2);
  } finally { session.restore(); }
});
