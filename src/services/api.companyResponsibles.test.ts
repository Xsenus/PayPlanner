import { afterEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

afterEach(() => vi.unstubAllGlobals());
it('responsible transport: preserves no-argument callers and accepts an uncached owned signal', async () => {
  const session = documentCopyTestSession();
  try {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => Response.json([])); vi.stubGlobal('fetch', fetch); const controller = new AbortController();
    await apiService.getActResponsibles(); await apiService.getActResponsibles({ signal: controller.signal });
    expect(new URL(String(fetch.mock.calls[1][0]), 'http://localhost').pathname).toMatch(/\/acts\/responsibles$/);
    expect(fetch.mock.calls[1][1]?.signal).toBe(controller.signal); expect(fetch.mock.calls.every(([, options]) => options?.cache === 'no-store')).toBe(true); expect(fetch).toHaveBeenCalledTimes(2);
  } finally { session.restore(); }
});
