import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { session.restore(); vi.unstubAllGlobals(); });
it.each([false, true])('legal lookup transport: preserves its payload with cancellation supplied=%s', async supplied => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json({ shortName: 'Организация' })); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController(); const payload = { query: '7701000000', kpp: '770101001' };
  const result = await apiService.findLegalEntityById(payload, supplied ? { signal: controller.signal } : undefined);
  const [url, options] = fetch.mock.calls[0]; expect(String(url)).toMatch(/\/legal-entities\/find-by-id$/);
  expect(options?.method).toBe('POST'); expect(options?.cache).toBe('no-store'); expect(options?.signal).toBe(supplied ? controller.signal : undefined);
  expect(JSON.parse(String(options?.body))).toEqual(payload); expect(result.shortName).toBe('Организация'); expect(fetch).toHaveBeenCalledOnce();
});
