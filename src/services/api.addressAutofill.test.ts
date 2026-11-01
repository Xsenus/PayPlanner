import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiService } from './api';
import { documentCopyTestSession } from '../test/documentCopySession';

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => { session.restore(); vi.unstubAllGlobals(); });
it.each(['suggest', 'clean'] as const)('preserves the %s address payload and forwards its cancellation signal', async kind => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(kind === 'suggest' ? [] : { result: 'Адрес' })); vi.stubGlobal('fetch', fetch);
  const controller = new AbortController();
  if (kind === 'suggest') await apiService.suggestClientAddresses('Адрес', 7, controller.signal); else await apiService.cleanAddress('Адрес', controller.signal);
  const [url, options] = fetch.mock.calls[0]; expect(String(url)).toMatch(kind === 'suggest' ? /\/clients\/dadata\/suggest-address$/ : /\/clients\/dadata\/clean-address$/);
  expect(options?.method).toBe('POST'); expect(options?.signal).toBe(controller.signal);
  expect(JSON.parse(String(options?.body))).toEqual(kind === 'suggest' ? { query: 'Адрес', limit: 7 } : { address: 'Адрес' }); expect(fetch).toHaveBeenCalledOnce();
});
